'use strict';

const { spawn } = require('node:child_process');

const MaxOutputBytes = 64 * 1024;
const TimeoutMilliseconds = 180_000;

function runVaultCli(assembly, arguments_, { cwd, signal }) {
    return new Promise((resolve, reject) => {
        const child = spawn('dotnet', [assembly, ...arguments_], {
            cwd, signal, shell: false, windowsHide: true,
            stdio: ['ignore', 'pipe', 'pipe']
        });
        let output = '';
        let errors = '';
        let outputBytes = 0;
        let settled = false;
        let timedOut = false;

        const timeout = setTimeout(() => {
            timedOut = true;
            child.kill();
        }, TimeoutMilliseconds);

        const settle = (error, value) => {
            if (settled) {
                return;
            }
            settled = true;
            clearTimeout(timeout);
            if (error) {
                reject(error);
            } else {
                resolve(value);
            }
        };

        const collect = (stream, sink) => {
            stream.on('data', chunk => {
                outputBytes += chunk.length;
                if (outputBytes > MaxOutputBytes) {
                    child.kill();
                    settle(new Error('The vault CLI output exceeds the supported limit.'));
                    return;
                }
                if (sink === 'stdout') {
                    output += chunk.toString('utf8');
                } else {
                    errors += chunk.toString('utf8');
                }
            });
        };

        collect(child.stdout, 'stdout');
        collect(child.stderr, 'stderr');
        child.once('error', error => settle(error));
        child.once('close', code => {
            if (timedOut) {
                settle(new Error('The vault CLI timed out without advancing the baseline.'));
            } else if (code !== 0) {
                settle(new Error('The vault CLI refused the operation: ' +
                    (errors.trim() || 'exit code ' + code)));
            } else {
                settle(null, output.trim());
            }
        });
    });
}

module.exports = { runVaultCli };
