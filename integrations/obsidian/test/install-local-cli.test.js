'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');
const { installLocal } = require('../scripts/install-local');

test('installer preflight uses the real .NET CLI before writing an Obsidian plugin', async t => {
    const assembly = path.resolve('tools/Praxis.VaultCli/bin/Release/net10.0/Praxis.VaultCli.dll');
    await fs.access(assembly);
    const temporary = await fs.mkdtemp(path.join(os.tmpdir(), 'praxis-live-trial-test-'));
    t.after(() => fs.rm(temporary, { force: true, recursive: true }));

    const vault = path.join(temporary, 'vault');
    const archives = path.join(temporary, 'private-archives');
    await fs.mkdir(path.join(vault, '.obsidian'), { recursive: true });
    await fs.mkdir(archives);
    const original = Buffer.from('# Original vault content\r\n');
    await fs.writeFile(path.join(vault, 'my-note.md'), original);

    const result = await installLocal({
        vaultPath: vault,
        archiveDirectory: archives,
        cliAssemblyPath: assembly
    });

    assert.deepEqual(await fs.readdir(result.pluginDirectory),
        ['data.json', 'main.js', 'manifest.json']);
    assert.deepEqual(await fs.readFile(path.join(vault, 'my-note.md')), original);
    assert.deepEqual(await fs.readdir(archives), []);
    assert.equal(JSON.parse(await fs.readFile(path.join(result.pluginDirectory, 'manifest.json'))).id,
        'praxis-vault');
    const settings = JSON.parse(await fs.readFile(path.join(result.pluginDirectory, 'data.json')));
    assert.equal(settings.backupDirectory, await fs.realpath(archives));
    assert.equal(settings.cliAssemblyPath, await fs.realpath(assembly));
});
