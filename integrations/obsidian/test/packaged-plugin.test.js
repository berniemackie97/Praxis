'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');
const { packagePlugin } = require('../scripts/package');

async function loadPackage(t) {
    const output = await fs.mkdtemp(path.join(os.tmpdir(), 'praxis-plugin-package-'));
    t.after(() => fs.rm(output, { recursive: true, force: true }));
    await packagePlugin(output);
    const source = await fs.readFile(path.join(output, 'main.js'), 'utf8');
    const manifest = JSON.parse(await fs.readFile(path.join(output, 'manifest.json'), 'utf8'));
    const notices = [];
    const opened = [];
    class FileSystemAdapter {
        getBasePath() { return '/unused/vault'; }
    }
    class TFile {
        constructor(filePath) { this.path = filePath; }
    }
    class TFolder {
        constructor(filePath) { this.path = filePath; }
    }
    class Plugin {
        constructor(app) {
            this.app = app;
            this.commands = [];
            this.events = [];
        }
        async loadData() { return {}; }
        async saveData(data) { this.saved = data; }
        addSettingTab(tab) { this.settingsTab = tab; }
        addCommand(command) { this.commands.push(command); }
        registerEvent(ref) { this.events.push(ref); }
    }
    class PluginSettingTab {
        constructor(app, plugin) { this.app = app; this.plugin = plugin; }
    }
    class Modal {
        constructor(app) {
            this.app = app;
            this.titleEl = { setText() {} };
            this.contentEl = { createEl: () => ({ style: {} }), empty() {} };
        }
        open() { opened.push(this); this.onOpen(); }
    }
    class Setting {
        setName() { return this; }
        setDesc() { return this; }
        addText() { return this; }
    }
    class Notice {
        constructor(message) { notices.push(message); }
    }
    const listeners = new Map();
    const app = {
        vault: {
            adapter: new FileSystemAdapter(),
            on(event, callback) {
                const handlers = listeners.get(event) || new Set();
                handlers.add(callback);
                listeners.set(event, handlers);
                return { off: () => handlers.delete(callback) };
            }
        },
        workspace: {
            onLayoutReady(callback) { this.readyCallback = callback; }
        }
    };
    const entry = { exports: {} };
    new vm.Script(source, { filename: 'praxis-vault/main.js' }).runInNewContext({
        module: entry,
        require: name => name === 'obsidian'
            ? { FileSystemAdapter, Modal, Notice, Plugin, PluginSettingTab, Setting, TFile }
            : require(name),
        Buffer,
        AbortController,
        setTimeout,
        clearTimeout,
        console
    });
    const plugin = new entry.exports(app);
    await plugin.onload();
    return {
        output, source, manifest, app, plugin, opened, notices, TFile, TFolder,
        emit(event, ...args) {
            for (const handler of listeners.get(event) || []) {
                handler(...args);
            }
        },
        async flush() {
            await new Promise(resolve => setImmediate(resolve));
        },
        unload() {
            plugin.onunload();
            for (const ref of plugin.events) {
                ref.off();
            }
        }
    };
}

test('package is reproducible and ships only a self-contained entry and manifest', async t => {
    const instance = await loadPackage(t);
    assert.deepEqual((await fs.readdir(instance.output)).sort(), ['main.js', 'manifest.json']);
    assert.equal(instance.manifest.id, 'praxis-vault');
    assert.equal(instance.manifest.isDesktopOnly, true);
    const second = await fs.mkdtemp(path.join(os.tmpdir(), 'praxis-plugin-repeat-'));
    t.after(() => fs.rm(second, { recursive: true, force: true }));
    await packagePlugin(second);
    assert.equal(await fs.readFile(path.join(second, 'main.js'), 'utf8'), instance.source);
    assert.match(instance.source, /"\.\/cli-process": function/);
    assert.match(instance.source, /"\.\/archive-session": function/);
    assert.match(instance.source, /"\.\/main": function/);
});

test('onload registers commands but defers vault events until layout is ready', async t => {
    const host = await loadPackage(t);
    assert.deepEqual(host.plugin.commands.map(command => command.id),
        ['capture-or-update', 'preview-update']);
    assert.equal(host.plugin.events.length, 0);
    const session = {
        changes: 0,
        changed() { this.changes++; },
        recordRename: async () => {}
    };
    host.plugin.session = session;
    host.plugin.sessionPromise = Promise.resolve(session);
    host.emit('modify', new host.TFile('before.md'));
    assert.equal(session.changes, 0);
    host.app.workspace.readyCallback();
    assert.equal(host.plugin.events.length, 4);
    host.emit('modify', new host.TFile('after.md'));
    assert.equal(session.changes, 1);
    host.unload();
    host.emit('modify', new host.TFile('after.md'));
    assert.equal(session.changes, 1);
});

test('file rename events are recorded; folder rename invalidates capture without guessing', async t => {
    const host = await loadPackage(t);
    const renames = [];
    const session = {
        changes: 0,
        changed() { this.changes++; },
        async recordRename(previous, current) { renames.push({ previous, current }); },
        close() {}
    };
    host.plugin.session = session;
    host.plugin.sessionPromise = Promise.resolve(session);
    host.app.workspace.readyCallback();
    host.emit('rename', new host.TFile('Notes/new.md'), 'Notes/old.md');
    await host.flush();
    assert.deepEqual(renames, [{ previous: 'Notes/old.md', current: 'Notes/new.md' }]);
    host.emit('rename', new host.TFolder('Moved'), 'Old');
    assert.equal(session.changes, 1);
    assert.match(host.notices.at(-1), /folder renames need explicit CLI mapping/);
    assert.equal(renames.length, 1);
    host.unload();
});

test('explicit commands use the configured session without modifying notes or auto-capturing', async t => {
    const host = await loadPackage(t);
    const calls = [];
    const session = {
        async plan() { calls.push('plan'); return 'No changes'; },
        async snapshot() {
            calls.push('snapshot');
            return { archive: path.join('/snapshots', 'test.zip') };
        },
        close() {}
    };
    host.plugin.session = session;
    host.plugin.sessionPromise = Promise.resolve(session);
    host.app.workspace.readyCallback();
    assert.deepEqual(calls, []);
    host.plugin.commands[1].callback();
    await host.flush();
    assert.deepEqual(calls, ['plan']);
    assert.equal(host.opened.length, 1);
    host.plugin.commands[0].callback();
    await host.flush();
    assert.deepEqual(calls, ['plan', 'snapshot']);
    assert.match(host.notices.at(-1), /verified archive saved as test.zip/);
    host.unload();
});

test('unloading during a command prevents another command from being started', async t => {
    const host = await loadPackage(t);
    let finish;
    const session = {
        closeCalls: 0,
        close() { this.closeCalls++; },
        snapshot: () => new Promise(resolve => { finish = resolve; })
    };
    host.plugin.session = session;
    host.plugin.sessionPromise = Promise.resolve(session);
    host.plugin.commands[0].callback();
    await host.flush();
    host.unload();
    assert.equal(session.closeCalls, 1);
    finish({ archive: '/snapshots/test.zip' });
    await host.flush();
    host.plugin.commands[0].callback();
    await host.flush();
    assert.notEqual(host.notices.at(-1), 'Praxis: verified archive saved as test.zip');
});
