'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');
const { ArchiveSession } = require('../archive-session');
const { runVaultCli } = require('../cli-process');

const Assembly = path.resolve('tools/Praxis.VaultCli/bin/Release/net10.0/Praxis.VaultCli.dll');

test('desktop archive session executes the actual CLI and preserves original vault bytes', async t => {
    await fs.access(Assembly);
    const root = await fs.mkdtemp(path.join(os.tmpdir(), 'praxis-obsidian-e2e-'));
    t.after(() => fs.rm(root, { force: true, recursive: true }));
    const vault = path.join(root, 'vault');
    const backup = path.join(root, 'backups');
    await fs.mkdir(vault);
    await fs.mkdir(backup);
    await fs.mkdir(path.join(vault, 'Notes'));
    await fs.mkdir(path.join(vault, 'media'));
    const note = path.join(vault, 'Notes', 'entry.md');
    const original = Buffer.from('# Entry\r\n![[../media/first.png]]\r\n');
    const image = Buffer.from([0, 255, 18, 4]);
    await fs.writeFile(note, original);
    await fs.writeFile(path.join(vault, 'media', 'first.png'), image);

    const session = await ArchiveSession.open({
        vaultPath: vault, backupDirectory: backup, cliAssemblyPath: Assembly
    });
    const first = await session.snapshot();
    const verifyFirst = await runVaultCli(Assembly, ['verify', first.archive], {
        cwd: backup, signal: new AbortController().signal
    });
    assert.match(verifyFirst, /Tracked 1 vault attachment references/);
    assert.deepEqual(await fs.readFile(note), original);
    assert.deepEqual(await fs.readdir(vault), ['Notes', 'media']);

    await fs.rename(path.join(vault, 'media', 'first.png'),
        path.join(vault, 'media', 'second.png'));
    session.changed();
    await session.recordRename('media/first.png', 'media/second.png');
    const updatedBody = '# Updated\n![[../media/second.png]]\n';
    await fs.writeFile(note, updatedBody);
    session.changed();

    assert.match(await session.plan(), /1 updated, 1 renamed/);
    const second = await session.snapshot();
    assert.notEqual(second.archive, first.archive);
    assert.match(await runVaultCli(Assembly, ['verify', second.archive], {
        cwd: backup, signal: new AbortController().signal
    }), /Tracked 1 vault attachment references/);

    const restored = path.join(root, 'restored');
    await runVaultCli(Assembly, ['restore', second.archive, restored], {
        cwd: backup, signal: new AbortController().signal
    });
    assert.equal(await fs.readFile(path.join(restored, 'Notes', 'entry.md'), 'utf8'),
        updatedBody);
    assert.deepEqual(await fs.readFile(path.join(restored, 'media', 'second.png')), image);
    assert.deepEqual(await fs.readFile(note, 'utf8'), updatedBody);

    await fs.writeFile(note, '# Removed link\n');
    session.changed();
    assert.match(await session.plan(), /1 removed/);
    await assert.rejects(session.snapshot(), /reference|removal|disappeared/i);

    const state = JSON.parse(await fs.readFile(path.join(backup,
        '.praxis-vault-session.json'), 'utf8'));
    assert.equal(state.current, path.basename(second.archive));
    assert.deepEqual(state.renames, []);
    assert.ok((await fs.readdir(backup)).filter(file => file.endsWith('.zip')).length >= 2);
    session.close();
});

test('folder move through real CLI retains nested files and tracked embedded links', async t => {
    await fs.access(Assembly);
    const root = await fs.mkdtemp(path.join(os.tmpdir(), 'praxis-folder-move-'));
    t.after(() => fs.rm(root, { recursive: true, force: true }));
    const vault = path.join(root, 'vault');
    const backup = path.join(root, 'backups');
    await fs.mkdir(path.join(vault, 'Old', 'media'), { recursive: true });
    await fs.mkdir(backup);
    const note = Buffer.from('# Memo\n![](media/photo.png)\n');
    const photo = Buffer.from([0, 255, 13, 3]);
    await fs.writeFile(path.join(vault, 'Old', 'memo.md'), note);
    await fs.writeFile(path.join(vault, 'Old', 'media', 'photo.png'), photo);

    const session = await ArchiveSession.open({
        vaultPath: vault, backupDirectory: backup, cliAssemblyPath: Assembly
    });
    const original = await session.snapshot();
    await fs.rename(path.join(vault, 'Old'), path.join(vault, 'Intermediate'));
    await session.recordRename('Old', 'Intermediate', true);
    await fs.rename(path.join(vault, 'Intermediate'), path.join(vault, 'Final'));
    await session.recordRename('Intermediate', 'Final', true);

    const plan = await session.plan();
    assert.match(plan, /0 added, 0 updated, 2 renamed/);
    assert.match(plan, /0 removed/);
    const updated = await session.snapshot();
    const verify = await runVaultCli(Assembly, ['verify', updated.archive], {
        cwd: backup, signal: new AbortController().signal
    });
    assert.match(verify, /Tracked 1 vault attachment references/);
    const destination = path.join(root, 'restored');
    await runVaultCli(Assembly, ['restore', updated.archive, destination], {
        cwd: backup, signal: new AbortController().signal
    });
    assert.deepEqual(await fs.readFile(path.join(destination, 'Final', 'memo.md')), note);
    assert.deepEqual(await fs.readFile(path.join(destination, 'Final', 'media', 'photo.png')), photo);
    assert.deepEqual(await fs.readFile(path.join(vault, 'Final', 'memo.md')), note);
    assert.equal((await fs.readdir(vault)).length, 1);
    assert.equal((await fs.readdir(backup)).filter(file => file.endsWith('.zip')).length, 2);
    assert.equal((await fs.readFile(original.archive)).length > 0, true);
    session.close();
});
