'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');
const { ArchiveSession } = require('../archive-session');

async function fixture(t, runner) {
    const root = await fs.mkdtemp(path.join(os.tmpdir(), 'praxis-obsidian-test-'));
    t.after(() => fs.rm(root, { recursive: true, force: true }));
    const vault = path.join(root, 'vault');
    const backup = path.join(root, 'backup');
    const assembly = path.join(root, 'Praxis.VaultCli.dll');
    await fs.mkdir(vault);
    await fs.mkdir(backup);
    await fs.writeFile(assembly, 'test assembly');
    const calls = [];
    const execute = runner || (async (_assembly, args) => {
        calls.push(args);
        if (args[0] === 'capture' || args[0] === 'update') {
            await fs.writeFile(args[0] === 'capture' ? args[2] : args[3], 'archive', { flag: 'wx' });
        }
        if (args[0] === 'verify') {
            assert.equal(await fs.readFile(args[1], 'utf8'), 'archive');
        }
        return args[0] === 'plan' ? 'Changes: 0 added, 1 updated, 0 renamed.' : 'Verified.';
    });
    const open = () => ArchiveSession.open({
        vaultPath: vault, backupDirectory: backup, cliAssemblyPath: assembly, runner: execute
    });
    return { root, vault, backup, assembly, calls, open };
}

async function state(backup) {
    return JSON.parse(await fs.readFile(path.join(backup, '.praxis-vault-session.json'), 'utf8'));
}

test('first capture tracks links without adding any files inside the vault', async t => {
    const f = await fixture(t);
    await fs.writeFile(path.join(f.vault, 'note.md'), 'original');
    const session = await f.open();
    const result = await session.snapshot();

    assert.match(path.basename(result.archive), /^praxis-\d{8}T\d{6}Z-[a-f0-9]{32}\.zip$/);
    assert.equal((await state(f.backup)).current, path.basename(result.archive));
    assert.deepEqual((await state(f.backup)).renames, []);
    assert.equal(await fs.readFile(path.join(f.vault, 'note.md'), 'utf8'), 'original');
    assert.deepEqual(await fs.readdir(f.vault), ['note.md']);
    assert.deepEqual(f.calls.map(args => args[0]), ['capture', 'verify']);
    assert.deepEqual(f.calls[0].slice(-1), ['--track-links']);
});

test('explicit dotnet executable path is passed to the CLI runner', async t => {
    const f = await fixture(t);
    const dotnet = path.join(f.root, 'dotnet');
    await fs.writeFile(dotnet, 'test executable');
    const invocations = [];
    const session = await ArchiveSession.open({
        vaultPath: f.vault,
        backupDirectory: f.backup,
        cliAssemblyPath: f.assembly,
        dotnetExecutablePath: dotnet,
        runner: async (_assembly, args, options) => {
            invocations.push({ command: args[0], dotnet: options.dotnetExecutablePath });
            if (args[0] === 'capture') {
                await fs.writeFile(args[2], 'archive', { flag: 'wx' });
            }
            return 'verified';
        }
    });
    await session.snapshot();

    assert.deepEqual(invocations, [
        { command: 'capture', dotnet: await fs.realpath(dotnet) },
        { command: 'verify', dotnet: await fs.realpath(dotnet) }
    ]);
});

test('plan before first capture refuses without writing state', async t => {
    const f = await fixture(t);
    const session = await f.open();
    await assert.rejects(session.plan(), /No baseline archive/);
    assert.deepEqual(await fs.readdir(f.backup), []);
});

test('file rename survives restart and reaches the CLI as an explicit mapping', async t => {
    const f = await fixture(t);
    const session = await f.open();
    const first = await session.snapshot();
    await session.recordRename('assets/old.png', 'assets/new.png');
    session.close();

    const restarted = await f.open();
    assert.equal((await state(f.backup)).renames[0].fromPath, 'assets/old.png');
    assert.equal(await restarted.plan(), 'Changes: 0 added, 1 updated, 0 renamed.');
    const planCall = f.calls.find(args => args[0] === 'plan');
    assert.ok(planCall.includes('--renames'));

    const current = await restarted.snapshot();
    const updateCall = f.calls.find(args => args[0] === 'update');
    assert.deepEqual(updateCall.slice(0, 4), ['update', await fs.realpath(f.vault), first.archive, current.archive]);
    assert.ok(updateCall.includes('--renames'));
    assert.equal((await state(f.backup)).current, path.basename(current.archive));
    assert.deepEqual((await state(f.backup)).renames, []);
    assert.equal(await fs.readFile(first.archive, 'utf8'), 'archive');
    assert.notEqual(first.archive, current.archive);
});

test('multi-step rename chains and swaps resolve to original identities', async t => {
    const f = await fixture(t);
    const session = await f.open();
    await session.snapshot();
    await session.recordRename('a.md', 'temp.md');
    await session.recordRename('b.md', 'a.md');
    await session.recordRename('temp.md', 'b.md');

    assert.deepEqual((await state(f.backup)).renames, [
        { fromPath: 'a.md', toPath: 'b.md' },
        { fromPath: 'b.md', toPath: 'a.md' }
    ]);
});

test('moving a file back clears a pending mapping; case-only changes remain', async t => {
    const f = await fixture(t);
    const session = await f.open();
    await session.snapshot();
    await session.recordRename('a.md', 'b.md');
    await session.recordRename('b.md', 'a.md');
    assert.deepEqual((await state(f.backup)).renames, []);
    await session.recordRename('a.md', 'A.md');
    assert.deepEqual((await state(f.backup)).renames,
        [{ fromPath: 'a.md', toPath: 'A.md' }]);
});

test('folder renames persist across restart as ordered explicit moves', async t => {
    const f = await fixture(t);
    const firstSession = await f.open();
    const baseline = await firstSession.snapshot();
    await firstSession.recordRename('Old', 'Middle', true);
    await firstSession.recordRename('Middle', 'Final', true);
    await firstSession.recordRename('Final/notes/a.md', 'Final/notes/b.md');
    firstSession.close();

    const mapping = (await state(f.backup)).renames;
    assert.deepEqual(mapping, [
        { fromPath: 'Old', toPath: 'Middle', isFolder: true },
        { fromPath: 'Middle', toPath: 'Final', isFolder: true },
        { fromPath: 'Final/notes/a.md', toPath: 'Final/notes/b.md' }
    ]);

    const restored = await f.open();
    const updated = await restored.snapshot();
    assert.notEqual(updated.archive, baseline.archive);
    const args = f.calls.find(call => call[0] === 'update');
    const renamesPath = args[args.indexOf('--renames') + 1];
    assert.match(renamesPath, /praxis-renames-/);
    assert.equal((await state(f.backup)).current, path.basename(updated.archive));
    assert.deepEqual((await state(f.backup)).renames, []);
});

test('folder event subsumes a burst of matching descendant events', async t => {
    const f = await fixture(t);
    const session = await f.open();
    await session.snapshot();
    const folder = session.recordRename('Old', 'New', true);
    const descendants = Array.from({ length: 150 }, (_, i) =>
        session.recordRename('Old/notes/file' + i + '.md', 'New/notes/file' + i + '.md'));
    await Promise.all([folder, ...descendants]);

    assert.deepEqual((await state(f.backup)).renames,
        [{ fromPath: 'Old', toPath: 'New', isFolder: true }]);

    await session.recordRename('New/notes/file0.md', 'New/notes/renamed.md');
    assert.deepEqual((await state(f.backup)).renames, [
        { fromPath: 'Old', toPath: 'New', isFolder: true },
        { fromPath: 'New/notes/file0.md', toPath: 'New/notes/renamed.md' }
    ]);
});

test('redundant descendant events after a restart do not multiply rename rules', async t => {
    const f = await fixture(t);
    const session = await f.open();
    await session.snapshot();
    await session.recordRename('Old', 'New', true);
    session.close();

    const resumed = await f.open();
    await resumed.recordRename('Old/one.md', 'New/one.md');
    await resumed.recordRename('Old/Sub', 'New/Sub', true);
    assert.deepEqual((await state(f.backup)).renames, [
        { fromPath: 'Old', toPath: 'New', isFolder: true }
    ]);
});

test('file moves before folder moves remain ordered by event identity', async t => {
    const f = await fixture(t);
    const session = await f.open();
    await session.snapshot();
    await session.recordRename('Old/note.md', 'Old/renamed.md');
    await session.recordRename('Old', 'New', true);
    assert.deepEqual((await state(f.backup)).renames, [
        { fromPath: 'Old/note.md', toPath: 'Old/renamed.md' },
        { fromPath: 'Old', toPath: 'New', isFolder: true }
    ]);
});

test('invalid folder event markers never reset an existing archive baseline', async t => {
    const f = await fixture(t);
    const session = await f.open();
    await session.snapshot();
    const snapshot = await state(f.backup);
    snapshot.renames = [{ fromPath: 'Old', toPath: 'New', isFolder: false }];
    const statePath = path.join(f.backup, '.praxis-vault-session.json');
    await fs.writeFile(statePath, JSON.stringify(snapshot));
    await assert.rejects(f.open(), /invalid rename mapping/);
    assert.equal((await state(f.backup)).current, snapshot.current);
});

test('failed update preserves last good baseline and queued renames', async t => {
    let fail = false;
    const f = await fixture(t, async (_assembly, args) => {
        if (fail && args[0] === 'update') {
            throw new Error('Missing previous file.');
        }
        if (args[0] === 'capture' || args[0] === 'update') {
            await fs.writeFile(args[0] === 'capture' ? args[2] : args[3], 'archive', { flag: 'wx' });
        }
        return 'ok';
    });
    const session = await f.open();
    const previous = await session.snapshot();
    await session.recordRename('a.md', 'b.md');
    fail = true;
    await assert.rejects(session.snapshot(), /Missing previous file/);

    assert.equal((await state(f.backup)).current, path.basename(previous.archive));
    assert.equal((await state(f.backup)).renames.length, 1);
    assert.equal(await fs.readFile(previous.archive, 'utf8'), 'archive');
});

test('vault events during capture refuse adoption and retain previous baseline', async t => {
    let session;
    let interfere = false;
    const f = await fixture(t, async (_assembly, args) => {
        if (args[0] === 'capture' || args[0] === 'update') {
            await fs.writeFile(args[0] === 'capture' ? args[2] : args[3], 'archive', { flag: 'wx' });
            if (interfere) {
                session.changed();
            }
        }
        return 'ok';
    });
    session = await f.open();
    const previous = await session.snapshot();
    interfere = true;
    await assert.rejects(session.snapshot(), /vault changed during capture/i);
    assert.equal((await state(f.backup)).current, path.basename(previous.archive));
});

test('missing or damaged session metadata never resets the last baseline', async t => {
    const f = await fixture(t);
    const session = await f.open();
    await session.snapshot();
    await fs.writeFile(path.join(f.backup, '.praxis-vault-session.json'), '{broken', 'utf8');
    await assert.rejects(f.open(), SyntaxError);
    assert.match(await fs.readFile(path.join(f.backup, '.praxis-vault-session.json'), 'utf8'), /broken/);
});

test('state cannot refer to another vault or escape with an archive name', async t => {
    const f = await fixture(t);
    const session = await f.open();
    await session.snapshot();
    const saved = await state(f.backup);
    saved.current = '../something.zip';
    await fs.writeFile(path.join(f.backup, '.praxis-vault-session.json'), JSON.stringify(saved));
    await assert.rejects(f.open(), /does not match/);
    saved.current = null;
    saved.vault = path.join(f.root, 'unrelated');
    await fs.writeFile(path.join(f.backup, '.praxis-vault-session.json'), JSON.stringify(saved));
    await assert.rejects(f.open(), /does not match/);
});

test('archive output inside the vault is forbidden after resolving actual paths', async t => {
    const f = await fixture(t);
    await assert.rejects(ArchiveSession.open({
        vaultPath: f.vault, backupDirectory: f.vault,
        cliAssemblyPath: f.assembly
    }), /outside the vault/);
});

test('competing process lock fails without changing baseline', async t => {
    const f = await fixture(t);
    const session = await f.open();
    const previous = await session.snapshot();
    const lockPath = path.join(f.backup, '.praxis-vault-session.lock');
    await fs.writeFile(lockPath, 'held');
    await assert.rejects(session.snapshot(), /session lock/);
    assert.equal((await state(f.backup)).current, path.basename(previous.archive));
    await fs.unlink(lockPath);
});

test('rename mappings are limited to valid relative paths', async t => {
    const f = await fixture(t);
    const session = await f.open();
    await session.snapshot();
    await assert.rejects(session.recordRename('../escape.md', 'safe.md'), /invalid vault-relative/);
    assert.deepEqual((await state(f.backup)).renames, []);
});

test('unloading while a CLI verification is pending never adopts the generated archive', async t => {
    let releaseVerification;
    let verificationStarted;
    const enteredVerification = new Promise(resolve => { verificationStarted = resolve; });
    let hold = false;
    const f = await fixture(t, async (_assembly, args) => {
        if (args[0] === 'capture' || args[0] === 'update') {
            await fs.writeFile(args[0] === 'capture' ? args[2] : args[3], 'archive', { flag: 'wx' });
        }
        if (hold && args[0] === 'verify') {
            verificationStarted();
            await new Promise(resolve => { releaseVerification = resolve; });
        }
        return 'ok';
    });
    const session = await f.open();
    const baseline = await session.snapshot();
    hold = true;
    const update = session.snapshot();
    await enteredVerification;
    session.close();
    releaseVerification();

    await assert.rejects(update, /vault changed during verification|unloaded/);
    assert.equal((await state(f.backup)).current, path.basename(baseline.archive));
    assert.equal(await fs.readFile(baseline.archive, 'utf8'), 'archive');
});

test('unloading during the first capture cannot create a baseline pointer', async t => {
    let session;
    const f = await fixture(t, async (_assembly, args) => {
        if (args[0] === 'capture') {
            await fs.writeFile(args[2], 'archive', { flag: 'wx' });
            session.close();
        }
        return 'ok';
    });
    session = await f.open();
    await assert.rejects(session.snapshot(), /vault changed during capture|unloaded/);
    assert.deepEqual((await fs.readdir(f.backup)).filter(name => name.endsWith('.json')), []);
});

test('closing the session refuses subsequent operations', async t => {
    const f = await fixture(t);
    const session = await f.open();
    await session.snapshot();
    await session.recordRename('Old', 'New', true);
    session.close();
    await assert.rejects(session.snapshot(), /unloaded/);
    await assert.rejects(session.recordRename('Old/a.md', 'New/a.md'), /unloaded/);
});

test('manifest declares desktop-only support and valid plugin entry point', async () => {
    const manifest = require('../manifest.json');
    assert.equal(manifest.id, 'praxis-vault');
    assert.equal(manifest.isDesktopOnly, true);
    assert.equal(typeof manifest.minAppVersion, 'string');
    assert.ok(manifest.version);
});
