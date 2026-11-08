'use strict';

const { randomUUID } = require('node:crypto');
const fs = require('node:fs/promises');
const path = require('node:path');
const { runVaultCli } = require('./cli-process');

const StateName = '.praxis-vault-session.json';
const LockName = '.praxis-vault-session.lock';
const MaxStateBytes = 256 * 1024;
const MaxRenames = 2_000;
const ArchiveName = /^praxis-\d{8}T\d{6}Z-[a-f0-9]{32}\.zip$/;

function inside(root, candidate) {
    const relative = path.relative(root, candidate);
    return relative === '' || relative !== '..' &&
        !relative.startsWith('..' + path.sep) && !path.isAbsolute(relative);
}

function filePath(value) {
    if (typeof value !== 'string' || value.length === 0 || value.length > 1024 ||
        value.startsWith('/') || value.includes('\\') || value.includes('\0') ||
        value.split('/').some(segment => !segment || segment === '.' || segment === '..')) {
        throw new Error('The renamed file has an invalid vault-relative path.');
    }
    return value;
}

class ArchiveSession {
    constructor(vault, backup, assembly, runner) {
        this.vault = vault;
        this.backup = backup;
        this.assembly = assembly;
        this.runner = runner;
        this.statePath = path.join(backup, StateName);
        this.lockPath = path.join(backup, LockName);
        this.serial = Promise.resolve();
        this.activity = 0;
        this.active = null;
        this.closed = false;
    }

    static async open({ vaultPath, backupDirectory, cliAssemblyPath, runner = runVaultCli }) {
        if (!path.isAbsolute(vaultPath) || !path.isAbsolute(backupDirectory) ||
            !path.isAbsolute(cliAssemblyPath)) {
            throw new Error('Configure absolute vault, archive-directory and CLI assembly paths.');
        }

        const [vault, backup, assembly] = await Promise.all([
            fs.realpath(vaultPath),
            fs.realpath(backupDirectory),
            fs.realpath(cliAssemblyPath)
        ]);
        if (inside(vault, backup)) {
            throw new Error('Archive state and snapshots must be outside the vault.');
        }

        if (!(await fs.stat(vault)).isDirectory() ||
            !(await fs.stat(backup)).isDirectory() ||
            !(await fs.stat(assembly)).isFile()) {
            throw new Error('The vault, archive directory or CLI assembly is not a supported filesystem entry.');
        }

        const session = new ArchiveSession(vault, backup, assembly, runner);
        await session._readState();
        return session;
    }

    changed() {
        this.activity++;
    }

    close() {
        this.closed = true;
        this.active?.abort();
    }

    async recordRename(from, to) {
        this.changed();
        filePath(from);
        filePath(to);
        return this._enqueue(async state => {
            if (state.current === null || from === to) {
                return;
            }

            const existing = state.renames.find(rule => rule.toPath === from);
            if (existing) {
                existing.toPath = to;
                if (existing.fromPath === existing.toPath) {
                    state.renames.splice(state.renames.indexOf(existing), 1);
                }
            } else {
                if (state.renames.length >= MaxRenames) {
                    throw new Error('Too many pending rename mappings. Review them using the vault CLI.');
                }

                state.renames.push({ fromPath: from, toPath: to });
            }

            await this._writeState(state);
        });
    }

    async plan() {
        const generation = this.activity;
        return this._enqueue(async state => {
            if (state.current === null) {
                throw new Error('No baseline archive exists. Capture a first snapshot.');
            }

            return this._withRenameFile(state.renames, async renameFile => {
                const args = ['plan', this.vault, path.join(this.backup, state.current)];
                if (renameFile) {
                    args.push('--renames', renameFile);
                }

                const output = await this._run(args);
                if (this.activity !== generation) {
                    throw new Error('The vault changed during planning. Run the preview again.');
                }

                return output;
            });
        });
    }

    async snapshot() {
        const generation = this.activity;
        return this._enqueue(async state => {
            const now = new Date().toISOString().replace(/[-:]/g, '').replace(/\.\d+Z$/, 'Z');
            const name = 'praxis-' + now + '-' + randomUUID().replace(/-/g, '') + '.zip';
            const destination = path.join(this.backup, name);
            const isUpdate = state.current !== null;
            const result = await this._withRenameFile(state.renames, async renameFile => {
                const args = isUpdate
                    ? ['update', this.vault, path.join(this.backup, state.current), destination]
                    : ['capture', this.vault, destination, '--track-links'];
                if (isUpdate && renameFile) {
                    args.push('--renames', renameFile);
                }

                const output = await this._run(args);
                if (this.activity !== generation) {
                    throw new Error('The vault changed during capture. The new archive was not adopted.');
                }

                await this._run(['verify', destination]);
                if (this.activity !== generation) {
                    throw new Error('The vault changed during verification. The new archive was not adopted.');
                }

                return output;
            });

            state.current = name;
            state.renames = [];
            await this._writeState(state);
            return { archive: destination, output: result };
        });
    }

    async _enqueue(action) {
        const task = this.serial.then(async () => {
            if (this.closed) {
                throw new Error('The adapter has been unloaded.');
            }

            let lock;
            try {
                lock = await fs.open(this.lockPath, 'wx', 0o600);
            } catch (error) {
                if (error.code === 'EEXIST') {
                    throw new Error('Another archive operation holds the session lock. Inspect the lock before retrying.');
                }
                throw error;
            }

            try {
                return await action(await this._readState());
            } finally {
                await lock.close();
                await fs.unlink(this.lockPath);
            }
        });
        this.serial = task.catch(() => {});
        return task;
    }

    async _readState() {
        let file;
        try {
            file = await fs.open(this.statePath, 'r');
        } catch (error) {
            if (error.code === 'ENOENT') {
                return { version: 1, vault: this.vault, current: null, renames: [] };
            }
            throw error;
        }

        let state;
        try {
            const info = await file.stat();
            if (!info.isFile() || info.size > MaxStateBytes) {
                throw new Error('The archive session state is invalid or oversized.');
            }
            state = JSON.parse(await file.readFile('utf8'));
        } finally {
            await file.close();
        }

        if (!state || state.version !== 1 || state.vault !== this.vault ||
            !Array.isArray(state.renames) || state.renames.length > MaxRenames ||
            !(state.current === null ||
                typeof state.current === 'string' && ArchiveName.test(state.current))) {
            throw new Error('The archive session state does not match this vault or version.');
        }

        for (const item of state.renames) {
            if (!item || Object.keys(item).sort().join(',') !== 'fromPath,toPath') {
                throw new Error('The archive session contains an invalid rename mapping.');
            }
            filePath(item.fromPath);
            filePath(item.toPath);
        }

        if (state.current !== null) {
            const info = await fs.lstat(path.join(this.backup, state.current));
            if (!info.isFile() || info.isSymbolicLink()) {
                throw new Error('The current archive is missing or is not a regular file.');
            }
        }

        return state;
    }

    async _writeState(state) {
        const data = Buffer.from(JSON.stringify(state) + '\n');
        if (data.length > MaxStateBytes) {
            throw new Error('The archive session state exceeds its size limit.');
        }

        const pending = path.join(this.backup, '.praxis-state-' + randomUUID() + '.pending');
        try {
            const handle = await fs.open(pending, 'wx', 0o600);
            try {
                await handle.writeFile(data);
                await handle.sync();
            } finally {
                await handle.close();
            }
            await fs.rename(pending, this.statePath);
        } finally {
            await fs.rm(pending, { force: true });
        }
    }

    async _withRenameFile(renames, action) {
        if (renames.length === 0) {
            return action(null);
        }

        const file = path.join(this.backup, '.praxis-renames-' + randomUUID() + '.json');
        try {
            await fs.writeFile(file, JSON.stringify(renames), { flag: 'wx', mode: 0o600 });
            return await action(file);
        } finally {
            await fs.rm(file, { force: true });
        }
    }

    async _run(args) {
        const controller = new AbortController();
        this.active = controller;
        try {
            return await this.runner(this.assembly, args, {
                cwd: this.backup, signal: controller.signal
            });
        } finally {
            if (this.active === controller) {
                this.active = null;
            }
        }
    }
}

module.exports = { ArchiveSession };
