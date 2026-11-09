'use strict';

const path = require('node:path');
const {
    FileSystemAdapter, Modal, Notice, Plugin, PluginSettingTab, Setting, TFile, TFolder
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
            .setName('.NET executable')
            .setDesc('Absolute path to dotnet. This avoids depending on the GUI application PATH.')
            .addText(field => field
                .setPlaceholder('/absolute/path/to/dotnet')
                .setValue(this.plugin.settings.dotnetExecutablePath)
                .onChange(value => this.plugin.saveSettings('dotnetExecutablePath', value)));
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
            cliAssemblyPath: typeof saved?.cliAssemblyPath === 'string' ? saved.cliAssemblyPath : '',
            dotnetExecutablePath: typeof saved?.dotnetExecutablePath === 'string' ? saved.dotnetExecutablePath : ''
        };
        this.sessionPromise = null;
        this.session = null;
        this.running = false;
        this.disposed = false;
        this.sessionEpoch = 0;
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
                if (file instanceof TFile || file instanceof TFolder) {
                    void this.recordRename(previousPath, file.path, file instanceof TFolder);
                } else {
                    this.session?.changed();
                    new Notice('Praxis: unsupported rename event needs manual review.');
                }
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
        this.sessionEpoch++;
        this.session?.close();
        this.session = null;
        this.sessionPromise = null;
        this.settings[name] = value.trim();
        await this.saveData(this.settings);
    }

    async getSession() {
        if (this.disposed) {
            throw new Error('The plugin was unloaded.');
        }

        const epoch = this.sessionEpoch;
        if (!this.sessionPromise) {
            if (!(this.app.vault.adapter instanceof FileSystemAdapter)) {
                throw new Error('The local vault adapter requires Obsidian desktop.');
            }

            const pending = ArchiveSession.open({
                vaultPath: this.app.vault.adapter.getBasePath(),
                backupDirectory: this.settings.backupDirectory,
                cliAssemblyPath: this.settings.cliAssemblyPath,
                dotnetExecutablePath: this.settings.dotnetExecutablePath || undefined
            }).then(session => {
                if (this.disposed || this.sessionEpoch !== epoch) {
                    session.close();
                    throw new Error('The plugin session configuration changed.');
                }

                this.session = session;
                return session;
            });
            const guarded = pending.catch(error => {
                if (this.sessionPromise === guarded) {
                    this.sessionPromise = null;
                }
                throw error;
            });
            this.sessionPromise = guarded;
        }

        const session = await this.sessionPromise;
        if (this.disposed || this.sessionEpoch !== epoch) {
            throw new Error('The plugin session configuration changed.');
        }

        return session;
    }

    async recordRename(previous, current, isFolder = false) {
        try {
            const session = await this.getSession();
            await session.recordRename(previous, current, isFolder);
        } catch (error) {
            new Notice('Praxis: rename tracking failed: ' + error.message);
        }
    }

    async runCommand(kind) {
        if (this.running) {
            new Notice('Praxis: an archive operation is already running.');
            return;
        }

        if (this.disposed) {
            return;
        }

        this.running = true;
        try {
            const session = await this.getSession();
            if (kind === 'plan') {
                const result = await session.plan();
                if (!this.disposed) {
                    new PlanModal(this.app, result).open();
                }
            } else {
                const result = await session.snapshot();
                if (!this.disposed) {
                    new Notice('Praxis: verified archive saved as ' +
                        path.basename(result.archive), 10_000);
                }
            }
        } catch (error) {
            if (!this.disposed) {
                new Notice('Praxis: ' + error.message, 12_000);
            }
        } finally {
            this.running = false;
        }
    }

    onunload() {
        this.disposed = true;
        this.sessionEpoch++;
        this.session?.close();
    }
};
