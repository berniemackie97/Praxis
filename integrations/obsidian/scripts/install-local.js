'use strict';

const fs = require('node:fs/promises');
const os = require('node:os');
const path = require('node:path');
const { packagePlugin } = require('./package');
const { runVaultCli } = require('../cli-process');

function contains(root, candidate) {
    const relative = path.relative(root, candidate);
    return relative === '' || relative !== '..' &&
        !relative.startsWith('..' + path.sep) && !path.isAbsolute(relative);
}

function configName(value) {
    if (typeof value !== 'string' || !/^[a-zA-Z0-9_.-]+$/.test(value) ||
        value === '.' || value === '..') {
        throw new Error('The Obsidian configuration directory must be a single directory name.');
    }
    return value;
}

async function preflight(assembly, run, temporary) {
    const source = path.join(temporary, 'vault');
    const archive = path.join(temporary, 'check.zip');
    const restored = path.join(temporary, 'restored');
    await fs.mkdir(source);
    const note = Buffer.from('# Trial\n![[picture.png]]\n');
    const picture = Buffer.from([0, 255, 14, 3]);
    await fs.writeFile(path.join(source, 'note.md'), note);
    await fs.writeFile(path.join(source, 'picture.png'), picture);
    const invoke = args => run(assembly, args, {
        cwd: temporary, signal: new AbortController().signal
    });
    await invoke(['capture', source, archive, '--track-links']);
    const verification = await invoke(['verify', archive]);
    if (!verification.includes('Tracked 1 vault attachment references')) {
        throw new Error('The CLI failed the tracked-reference preflight.');
    }
    const links = await invoke(['links', archive]);
    if (!links.includes('Resolved 1 note-to-attachment links; 0 unresolved')) {
        throw new Error('The CLI failed the attachment-link preflight.');
    }
    await invoke(['restore', archive, restored]);
    if (!(await fs.readFile(path.join(restored, 'note.md'))).equals(note) ||
        !(await fs.readFile(path.join(restored, 'picture.png'))).equals(picture)) {
        throw new Error('The CLI failed the byte-exact restore preflight.');
    }
}

async function installLocal({ vaultPath, archiveDirectory, cliAssemblyPath,
    configDirectory = '.obsidian' }, run = runVaultCli) {
    if (![vaultPath, archiveDirectory, cliAssemblyPath].every(value =>
        typeof value === 'string' && path.isAbsolute(value))) {
        throw new Error('Vault, archive directory and CLI assembly paths must be absolute.');
    }
    configName(configDirectory);
    const [vault, archives, assembly] = await Promise.all([
        fs.realpath(vaultPath),
        fs.realpath(archiveDirectory),
        fs.realpath(cliAssemblyPath)
    ]);
    if (contains(vault, archives) || contains(archives, vault)) {
        throw new Error('The archive directory must be separate from the vault.');
    }
    if (path.basename(assembly) !== 'Praxis.VaultCli.dll' ||
        !(await fs.stat(vault)).isDirectory() ||
        !(await fs.stat(archives)).isDirectory() ||
        !(await fs.stat(assembly)).isFile()) {
        throw new Error('The selected paths do not identify a vault, archive directory and published CLI.');
    }

    const configuration = path.join(vault, configDirectory);
    const configInfo = await fs.lstat(configuration);
    if (!configInfo.isDirectory() || configInfo.isSymbolicLink()) {
        throw new Error('The Obsidian configuration directory must not be a link.');
    }
    const plugins = path.join(configuration, 'plugins');
    try {
        const pluginsInfo = await fs.lstat(plugins);
        if (!pluginsInfo.isDirectory() || pluginsInfo.isSymbolicLink()) {
            throw new Error('The Obsidian plugins directory must not be a link.');
        }
    } catch (error) {
        if (error.code !== 'ENOENT') {
            throw error;
        }
    }
    const destination = path.join(plugins, 'praxis-vault');
    try {
        await fs.lstat(destination);
        throw new Error('Praxis Vault already exists. Installation never overwrites a plugin.');
    } catch (error) {
        if (error.code !== 'ENOENT') {
            throw error;
        }
    }

    const temporary = await fs.mkdtemp(path.join(os.tmpdir(), 'praxis-trial-'));
    try {
        await preflight(assembly, run, temporary);
        const packageDirectory = path.join(temporary, 'package');
        await packagePlugin(packageDirectory);

        await fs.mkdir(plugins, { recursive: true });
        const pluginsInfo = await fs.lstat(plugins);
        if (!pluginsInfo.isDirectory() || pluginsInfo.isSymbolicLink()) {
            throw new Error('The plugin directory changed during the preflight.');
        }

        await fs.mkdir(destination, { mode: 0o700 });
        try {
            await fs.copyFile(path.join(packageDirectory, 'main.js'),
                path.join(destination, 'main.js'));
            await fs.copyFile(path.join(packageDirectory, 'manifest.json'),
                path.join(destination, 'manifest.json'));
            await fs.writeFile(path.join(destination, 'data.json'), JSON.stringify({
                backupDirectory: archives, cliAssemblyPath: assembly
            }, null, 2) + '\n', { flag: 'wx', mode: 0o600 });
        } catch (error) {
            await fs.rm(destination, { force: true, recursive: true });
            throw error;
        }
        return { pluginDirectory: destination, archiveDirectory: archives };
    } finally {
        await fs.rm(temporary, { force: true, recursive: true });
    }
}

function parseArguments(argv) {
    const keys = new Map([
        ['--vault', 'vaultPath'],
        ['--archives', 'archiveDirectory'],
        ['--cli', 'cliAssemblyPath'],
        ['--config-dir', 'configDirectory']
    ]);
    const result = {};
    for (let index = 0; index < argv.length; index += 2) {
        const key = keys.get(argv[index]);
        if (!key || index + 1 >= argv.length || Object.hasOwn(result, key)) {
            throw new Error('Usage: install-local.js --vault <absolute-path> --archives <absolute-path> --cli <absolute-Praxis.VaultCli.dll> [--config-dir <name>]');
        }
        result[key] = argv[index + 1];
    }
    return result;
}

if (require.main === module) {
    try {
        const options = parseArguments(process.argv.slice(2));
        installLocal(options).then(result => {
            console.log('Preflight passed and installed Praxis Vault at ' + result.pluginDirectory);
            console.log('Enable the plugin in Obsidian Community plugins, using a disposable vault first.');
        }).catch(error => {
            console.error('Installation refused: ' + error.message);
            process.exitCode = 1;
        });
    } catch (error) {
        console.error(error.message);
        process.exitCode = 2;
    }
}

module.exports = { installLocal, parseArguments };
