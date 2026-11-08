'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');
const { installLocal, parseArguments } = require('../scripts/install-local');

async function fixture(t) {
    const root = await fs.mkdtemp(path.join(os.tmpdir(), 'praxis-install-test-'));
    t.after(() => fs.rm(root, { force: true, recursive: true }));
    const vault = path.join(root, 'vault');
    const archives = path.join(root, 'archives');
    const cli = path.join(root, 'published', 'Praxis.VaultCli.dll');
    const dotnet = path.join(root, 'published', 'dotnet');
    await fs.mkdir(path.join(vault, '.obsidian'), { recursive: true });
    await fs.mkdir(archives);
    await fs.mkdir(path.dirname(cli));
    await fs.writeFile(cli, 'stub assembly');
    await fs.writeFile(dotnet, 'stub host');
    await fs.writeFile(path.join(vault, 'private.md'), '# Personal note');
    const calls = [];
    async function run(_assembly, args) {
        calls.push(args);
        if (args[0] === 'capture') {
            await fs.writeFile(args[2], 'archive');
        } else if (args[0] === 'verify') {
            return 'Verified 1 notes and 1 attachments.\nTracked 1 vault attachment references.';
        } else if (args[0] === 'links') {
            return 'Resolved 1 note-to-attachment links; 0 unresolved.';
        } else if (args[0] === 'restore') {
            await fs.mkdir(args[2]);
            await fs.writeFile(path.join(args[2], 'note.md'), '# Trial\n![[picture.png]]\n');
            await fs.writeFile(path.join(args[2], 'picture.png'), Buffer.from([0, 255, 14, 3]));
        }
        return 'ok';
    }
    return {
        root, vault, archives, cli, dotnet, calls, run,
        options: { vaultPath: vault, archiveDirectory: archives,
            cliAssemblyPath: cli, dotnetExecutablePath: dotnet }
    };
}

test('preflight succeeds before installing a two-file plugin with external archive settings', async t => {
    const f = await fixture(t);
    const result = await installLocal(f.options, f.run);
    assert.deepEqual(f.calls.map(args => args[0]), ['capture', 'verify', 'links', 'restore']);
    assert.equal(result.pluginDirectory, path.join(await fs.realpath(f.vault), '.obsidian', 'plugins', 'praxis-vault'));
    assert.deepEqual((await fs.readdir(result.pluginDirectory)).sort(),
        ['data.json', 'main.js', 'manifest.json']);
    const settings = JSON.parse(await fs.readFile(path.join(result.pluginDirectory, 'data.json'), 'utf8'));
    assert.equal(settings.backupDirectory, await fs.realpath(f.archives));
    assert.equal(settings.cliAssemblyPath, await fs.realpath(f.cli));
    assert.equal(settings.dotnetExecutablePath, await fs.realpath(f.dotnet));
    assert.equal(await fs.readFile(path.join(f.vault, 'private.md'), 'utf8'), '# Personal note');
    assert.deepEqual(await fs.readdir(f.archives), []);
    assert.match(await fs.readFile(path.join(result.pluginDirectory, 'main.js'), 'utf8'),
        /module\.exports = load\('\.\/main'\)/);
});

test('existing plugins are not overwritten, even if CLI would pass', async t => {
    const f = await fixture(t);
    const existing = path.join(f.vault, '.obsidian', 'plugins', 'praxis-vault');
    await fs.mkdir(existing, { recursive: true });
    await fs.writeFile(path.join(existing, 'data.json'), '{"keep":"this"}');
    await assert.rejects(installLocal(f.options, f.run), /already exists/);
    assert.equal(await fs.readFile(path.join(existing, 'data.json'), 'utf8'), '{"keep":"this"}');
    assert.deepEqual(f.calls, []);
});

test('failed restore preflight does not install a plugin or alter original vault files', async t => {
    const f = await fixture(t);
    const run = async (assembly, args, context) => {
        if (args[0] === 'restore') {
            throw new Error('Restore preflight refused');
        }
        return f.run(assembly, args, context);
    };
    await assert.rejects(installLocal(f.options, run), /Restore preflight refused/);
    await assert.rejects(fs.stat(path.join(f.vault, '.obsidian', 'plugins', 'praxis-vault')),
        { code: 'ENOENT' });
    assert.equal(await fs.readFile(path.join(f.vault, 'private.md'), 'utf8'), '# Personal note');
    assert.deepEqual(await fs.readdir(f.archives), []);
});

test('archive location inside vault or vault inside archives is rejected before any writes', async t => {
    const f = await fixture(t);
    await assert.rejects(installLocal({ ...f.options,
        archiveDirectory: path.join(f.vault, '.obsidian') }, f.run), /separate from the vault/);
    await assert.rejects(installLocal({ ...f.options, archiveDirectory: f.root }, f.run),
        /separate from the vault/);
    assert.deepEqual(f.calls, []);
});

test('installer supports a non-default existing Obsidian config directory', async t => {
    const f = await fixture(t);
    await fs.mkdir(path.join(f.vault, '.custom-config'));
    const result = await installLocal({ ...f.options, configDirectory: '.custom-config' }, f.run);
    assert.equal(result.pluginDirectory,
        path.join(await fs.realpath(f.vault), '.custom-config', 'plugins', 'praxis-vault'));
    assert.equal(await fs.readFile(path.join(f.vault, 'private.md'), 'utf8'), '# Personal note');
});

test('symlinked configuration is refused rather than escaping vault', async t => {
    const f = await fixture(t);
    await fs.symlink(f.archives, path.join(f.vault, '.external-config'), 'dir');
    await assert.rejects(installLocal({
        ...f.options, configDirectory: '.external-config'
    }, f.run), /must not be a link/);
    assert.deepEqual(f.calls, []);
});

test('installation arguments reject duplicates and unknown parameters', () => {
    assert.deepEqual(parseArguments(['--vault', '/v', '--archives', '/a', '--cli', '/c',
        '--dotnet', '/d']),
        { vaultPath: '/v', archiveDirectory: '/a', cliAssemblyPath: '/c', dotnetExecutablePath: '/d' });
    assert.throws(() => parseArguments(['--vault', '/v', '--vault', '/q']), /Usage:/);
    assert.throws(() => parseArguments(['--unknown', 'x']), /Usage:/);
    assert.throws(() => parseArguments(['--vault']), /Usage:/);
});
