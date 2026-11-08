'use strict';

const fs = require('node:fs/promises');
const path = require('node:path');
const { randomUUID } = require('node:crypto');

const root = path.resolve(__dirname, '..');
const modules = [
    ['cli-process', 'cli-process.js'],
    ['archive-session', 'archive-session.js'],
    ['main', 'main.js']
];
const externals = new Set([
    'obsidian', 'node:path', 'node:crypto', 'node:fs/promises', 'node:child_process'
]);

async function packagePlugin(outputDirectory) {
    const manifest = JSON.parse(await fs.readFile(path.join(root, 'manifest.json'), 'utf8'));
    if (manifest.id !== 'praxis-vault' || manifest.isDesktopOnly !== true) {
        throw new Error('The desktop plugin manifest is invalid.');
    }

    const fragments = [];
    for (const [name, file] of modules) {
        const source = await fs.readFile(path.join(root, file), 'utf8');
        for (const match of source.matchAll(/\brequire\(([^)]+)\)/g)) {
            const specifier = match[1];
            if (!/^['"][^'"]+['"]$/.test(specifier)) {
                throw new Error('Dynamic module resolution is not permitted in the desktop plugin.');
            }

            const dependency = specifier.slice(1, -1);
            if (!externals.has(dependency) &&
                !modules.some(([local]) => dependency === './' + local)) {
                throw new Error('Unexpected module dependency: ' + dependency);
            }
        }

        fragments.push(JSON.stringify('./' + name) +
            ': function(module, exports, require) {\n' + source + '\n}');
    }

    const bundle = [
        "'use strict';",
        'const modules = {',
        fragments.join(',\n'),
        '};',
        'const cache = Object.create(null);',
        'function load(specifier) {',
        '    if (!Object.hasOwn(modules, specifier)) {',
        '        if (!' + JSON.stringify([...externals]) + '.includes(specifier)) {',
        '            throw new Error("Unexpected plugin dependency: " + specifier);',
        '        }',
        '        return require(specifier);',
        '    }',
        '    if (!Object.hasOwn(cache, specifier)) {',
        '        const entry = { exports: {} };',
        '        cache[specifier] = entry;',
        '        modules[specifier](entry, entry.exports, load);',
        '    }',
        '    return cache[specifier].exports;',
        '}',
        "module.exports = load('./main');",
        ''
    ].join('\n');

    const directory = path.resolve(outputDirectory);
    if (directory === root || directory.startsWith(root + path.sep)) {
        throw new Error('The package destination must not overlap the plugin source directory.');
    }

    const parent = path.dirname(directory);
    await fs.mkdir(parent, { recursive: true });
    const pending = path.join(parent, '.' + path.basename(directory) +
        '-' + randomUUID() + '.pending');
    await fs.mkdir(pending);
    try {
        await fs.writeFile(path.join(pending, 'main.js'), bundle, { flag: 'wx' });
        await fs.copyFile(path.join(root, 'manifest.json'), path.join(pending, 'manifest.json'));
        try {
            await fs.lstat(directory);
            throw new Error('The package destination already exists.');
        } catch (error) {
            if (error.code !== 'ENOENT') {
                throw error;
            }
        }
        await fs.rename(pending, directory);
    } finally {
        await fs.rm(pending, { recursive: true, force: true });
    }
    return directory;
}

if (require.main === module) {
    if (process.argv.length !== 3) {
        console.error('Usage: node integrations/obsidian/scripts/package.js <output-directory>');
        process.exitCode = 2;
    } else {
        packagePlugin(process.argv[2]).catch(error => {
            console.error(error.message);
            process.exitCode = 1;
        });
    }
}

module.exports = { packagePlugin };
