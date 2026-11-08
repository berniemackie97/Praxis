'use strict';

const path = require('node:path');
const {
    FileSystemAdapter, Modal, Notice, Plugin, PluginSettingTab, Setting, TFile
} = require('obsidian');
const { ArchiveSession } = require('./archive-session');

class PlanModal extends Modal {
    constructor(app, result) {
        super(app);
        this.result = result;
    }

    onOpen() {
        this.titleEl.setText('Praxis vault update preview');
        const body = this.contentEl.createEl('pre', { text: this.result });
        body.style.whiteSpace = 'pre-wrap';
        body.style.maxHeight = '60vh';
        body.style.overflowY = 'auto';
    }

    onClose() {
        this.contentEl.empty();
    }
}

class PraxisSettings extends PluginSettingTab {
    constructor(app, plugin) {
        super(app, plugin);
        this.plugin = plugin;
    }

    display() {
        this.containerEl.empty();
        new Setting(this.containerEl)
            .setName('Archive directory')
            .setDesc('Existing absolute directory outside this vault. Archives and session state are stored here.')
            .addText(field => field
                .setPlaceholder('/path/to/private/archives')
                .setValue(this.plugin.settings.backupDirectory)
                .onChange(value => this.plugin.saveSettings('backupDirectory', value)));
        new Setting(this.containerEl)
            .setName('Vault CLI assembly')
            .setDesc('Absolute path to the published Praxis.VaultCli.dll; requires the matching .NET runtime.')
            .addText(field => field
                .setPlaceholder('/path/to/Praxis.VaultCli.dll')
                .setValue(this.plugin.settings.cliAssemblyPath)
                .onChange(value => this.plugin.saveSettings('cliAssemblyPath', value)));
    }
}

module.exports = class PraxisPlugin extends Plugin {
    async onload() {
        const saved = await this.loadData();
        this.settings = {
            backupDirectory: typeof saved?.backupDirectory === 'string' ? saved.backupDirectory : '',
            cliAssemblyPath: typeof saved?.cliAssemblyPath === 'string' ? saved.cliAssemblyPath : ''
        };
        this.sessionPromise = null;
        this.session = null;
        this.running = false;
        this.disposed = false;
        this.addSettingTab(new PraxisSettings(this.app, this));

        this.addCommand({
            id: 'capture-or-update',
            name: 'Capture or update local archive',
            callback: () => void this.runCommand('snapshot')
        });
        this.addCommand({
            id: 'preview-update',
            name: 'Preview local archive update',
            callback: () => void this.runCommand('plan')
        });

        this.app.workspace.onLayoutReady(() => {
            if (this.disposed) {
                return;
            }

            this.registerEvent(this.app.vault.on('rename', (file, previousPath) => {
                if (!(file instanceof TFile)) {
                    this.session?.changed();
                    new Notice('Praxis: folder renames need explicit CLI mapping before the next update.');
                    return;
                }

                void this.recordRename(previousPath, file.path);
            }));
            for (const event of ['create', 'modify', 'delete']) {
                this.registerEvent(this.app.vault.on(event, () => this.session?.changed()));
            }
        });
    }

    async saveSettings(name, value) {
        if (this.running) {
            new Notice('Praxis: finish the active archive operation before changing settings.');
            return;
        }
        this.session?.close();
        this.session = null;
        this.sessionPromise = null;
        this.settings[name] = value.trim();
        await this.saveData(this.settings);
    }

    async getSession() {
        if (!this.sessionPromise) {
            if (!(this.app.vault.adapter instanceof FileSystemAdapter)) {
                throw new Error('The local vault adapter requires Obsidian desktop.');
            }
            this.sessionPromise = ArchiveSession.open({
                vaultPath: this.app.vault.adapter.getBasePath(),
                backupDirectory: this.settings.backupDirectory,
                cliAssemblyPath: this.settings.cliAssemblyPath
            }).then(session => {
                if (this.disposed) {
                    session.close();
                    throw new Error('The plugin was unloaded.');
                }
                this.session = session;
                return session;
            }).catch(error => {
                this.sessionPromise = null;
                throw error;
            });
        }
        return this.sessionPromise;
    }

    async recordRename(previous, current) {
        try {
            const session = await this.getSession();
            await session.recordRename(previous, current);
        } catch (error) {
            new Notice('Praxis: rename tracking failed: ' + error.message);
        }
    }

    async runCommand(kind) {
        if (this.running) {
            new Notice('Praxis: an archive operation is already running.');
            return;
        }

        this.running = true;
        try {
            const session = await this.getSession();
            if (kind === 'plan') {
                new PlanModal(this.app, await session.plan()).open();
            } else {
                const result = await session.snapshot();
                new Notice('Praxis: verified archive saved as ' +
                    path.basename(result.archive), 10_000);
            }
        } catch (error) {
            new Notice('Praxis: ' + error.message, 12_000);
        } finally {
            this.running = false;
        }
    }

    onunload() {
        this.disposed = true;
        this.session?.close();
    }
};
