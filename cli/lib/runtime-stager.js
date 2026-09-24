'use strict';

const fs = require('fs');
const path = require('path');
const os = require('os');
const crypto = require('crypto');
const { execFileSync } = require('child_process');

function resolveDefaultRuntimeRoot() {
    if (process.env.GENEXUS_MCP_RUNTIME_DIR) {
        return process.env.GENEXUS_MCP_RUNTIME_DIR;
    }
    const localAppData = process.env.LOCALAPPDATA || path.join(os.homedir(), 'AppData', 'Local');
    return path.join(localAppData, 'GenexusMCP', 'runtime');
}

function isCheckoutMode(packageRoot) {
    try {
        const gitMarker = fs.statSync(path.join(packageRoot, '.git'));
        return gitMarker.isDirectory() || gitMarker.isFile();
    } catch {
        return false;
    }
}

function shouldStage(packageRoot = path.resolve(__dirname, '..', '..')) {
    if (process.env.GENEXUS_MCP_GATEWAY_EXE) {
        return false;
    }
    if (process.env.GENEXUS_MCP_NO_STAGING === '1') {
        return false;
    }
    if (process.env.GENEXUS_MCP_FORCE_STAGING === '1') {
        return true;
    }
    return !isCheckoutMode(packageRoot);
}

function computeFileSha256(filePath) {
    const hash = crypto.createHash('sha256');
    const buffer = fs.readFileSync(filePath);
    hash.update(buffer);
    return hash.digest('hex');
}

function computePublishFingerprint(publishDir) {
    const manifestPath = path.join(publishDir, 'gxmcp-manifest.json');
    if (fs.existsSync(manifestPath)) {
        try {
            return computeFileSha256(manifestPath).slice(0, 8);
        } catch {
            // fallback
        }
    }
    const gatewayExe = path.join(publishDir, 'GxMcp.Gateway.exe');
    if (fs.existsSync(gatewayExe)) {
        try {
            return computeFileSha256(gatewayExe).slice(0, 8);
        } catch {
            // fallback
        }
    }
    return '00000000';
}

function verifyManifest(stagedDir, manifestPath) {
    if (!fs.existsSync(manifestPath)) {
        return;
    }
    let manifest;
    try {
        manifest = JSON.parse(fs.readFileSync(manifestPath, 'utf8'));
    } catch (err) {
        throw new Error(`Failed to parse manifest at ${manifestPath}: ${err.message}`);
    }

    const artifacts = Array.isArray(manifest.artifacts) ? manifest.artifacts : [];
    for (const artifact of artifacts) {
        if (!artifact || !artifact.path) continue;
        const fullPath = path.join(stagedDir, artifact.path);
        if (!fs.existsSync(fullPath)) {
            throw new Error(`Manifest verification failed: missing artifact '${artifact.path}'`);
        }
        if (typeof artifact.size === 'number') {
            const stat = fs.statSync(fullPath);
            if (stat.size !== artifact.size) {
                throw new Error(`Manifest verification failed: size mismatch for '${artifact.path}' (expected ${artifact.size}, got ${stat.size})`);
            }
        }
        if (artifact.sha256) {
            const actualSha = computeFileSha256(fullPath);
            if (actualSha.toLowerCase() !== artifact.sha256.toLowerCase()) {
                throw new Error(`Manifest verification failed: sha256 mismatch for '${artifact.path}'`);
            }
        }
    }
}

function cleanOldRuntimes(runtimeRoot, currentTargetDir, keepCount = 2) {
    try {
        if (!fs.existsSync(runtimeRoot)) return;
        const entries = fs.readdirSync(runtimeRoot, { withFileTypes: true });

        // Clean up stale tmp directories older than 10 minutes
        const now = Date.now();
        for (const entry of entries) {
            if (entry.isDirectory() && entry.name.includes('.tmp-')) {
                const fullTmpPath = path.join(runtimeRoot, entry.name);
                try {
                    const stat = fs.statSync(fullTmpPath);
                    if (now - stat.mtimeMs > 10 * 60 * 1000) {
                        fs.rmSync(fullTmpPath, { recursive: true, force: true });
                    }
                } catch {
                    // Ignore removal failures on locked tmp dirs
                }
            }
        }

        // Gather valid version directories
        const versionDirs = [];
        for (const entry of entries) {
            if (!entry.isDirectory() || entry.name.includes('.tmp-')) continue;
            const fullPath = path.join(runtimeRoot, entry.name);
            try {
                const stat = fs.statSync(fullPath);
                versionDirs.push({
                    name: entry.name,
                    path: fullPath,
                    mtimeMs: stat.mtimeMs
                });
            } catch {
                // Ignore stat errors
            }
        }

        // Sort newest first
        versionDirs.sort((a, b) => b.mtimeMs - a.mtimeMs);

        // Keep current targetDir always, plus up to keepCount newest
        const normalizedCurrent = path.resolve(currentTargetDir).toLowerCase();
        let kept = 0;
        const toDelete = [];

        for (const vdir of versionDirs) {
            const normalized = path.resolve(vdir.path).toLowerCase();
            if (normalized === normalizedCurrent) {
                continue;
            }
            if (kept < keepCount) {
                kept++;
            } else {
                toDelete.push(vdir.path);
            }
        }

        for (const dirToDelete of toDelete) {
            try {
                fs.rmSync(dirToDelete, { recursive: true, force: true });
            } catch {
                // EBUSY or EPERM means another running process has loaded DLLs from this runtime.
                // Keep it for later cleanup.
            }
        }
    } catch {
        // Garbage collection is best-effort
    }
}

function ensureStagedGateway(options = {}) {
    const packageRoot = options.packageRoot || path.resolve(__dirname, '..', '..');
    const publishDir = options.publishDir || path.join(packageRoot, 'publish');
    const defaultGatewayExe = options.defaultGatewayExe || path.join(publishDir, 'GxMcp.Gateway.exe');

    if (!options.forceStaging && !shouldStage(packageRoot)) {
        return {
            staged: false,
            gatewayExePath: process.env.GENEXUS_MCP_GATEWAY_EXE || defaultGatewayExe,
            runtimeDir: path.dirname(process.env.GENEXUS_MCP_GATEWAY_EXE || defaultGatewayExe),
            reason: process.env.GENEXUS_MCP_GATEWAY_EXE ? 'GENEXUS_MCP_GATEWAY_EXE' : (process.env.GENEXUS_MCP_NO_STAGING === '1' ? 'NO_STAGING' : 'checkout')
        };
    }

    let version = '0.0.0';
    try {
        const pkg = JSON.parse(fs.readFileSync(path.join(packageRoot, 'package.json'), 'utf8'));
        if (pkg.version) version = pkg.version;
    } catch {
        // Fallback
    }

    const sha8 = computePublishFingerprint(publishDir);
    const runtimeRoot = options.runtimeRoot || resolveDefaultRuntimeRoot();
    const targetDir = path.join(runtimeRoot, `${version}-${sha8}`);
    const stagedGatewayExe = path.join(targetDir, 'GxMcp.Gateway.exe');

    if (fs.existsSync(stagedGatewayExe)) {
        cleanOldRuntimes(runtimeRoot, targetDir);
        return {
            staged: true,
            gatewayExePath: stagedGatewayExe,
            runtimeDir: targetDir,
            fresh: false
        };
    }

    if (!fs.existsSync(publishDir)) {
        throw new Error(`Cannot stage runtime: publish directory not found at ${publishDir}`);
    }

    fs.mkdirSync(runtimeRoot, { recursive: true });
    const tmpDir = `${targetDir}.tmp-${process.pid}-${Date.now()}`;
    fs.mkdirSync(tmpDir, { recursive: true });

    try {
        fs.cpSync(publishDir, tmpDir, { recursive: true });
        const manifestPath = path.join(tmpDir, 'gxmcp-manifest.json');
        verifyManifest(tmpDir, manifestPath);

        try {
            fs.renameSync(tmpDir, targetDir);
        } catch (renameErr) {
            // Concurrent launcher might have completed the rename first
            if (fs.existsSync(stagedGatewayExe)) {
                try {
                    fs.rmSync(tmpDir, { recursive: true, force: true });
                } catch {
                    // Ignore tmp cleanup error
                }
            } else {
                throw renameErr;
            }
        }
    } catch (err) {
        try {
            fs.rmSync(tmpDir, { recursive: true, force: true });
        } catch {
            // Ignore tmp cleanup error
        }
        throw err;
    }

    cleanOldRuntimes(runtimeRoot, targetDir);

    return {
        staged: true,
        gatewayExePath: stagedGatewayExe,
        runtimeDir: targetDir,
        fresh: true
    };
}

function listStagedRuntimes(runtimeRoot = resolveDefaultRuntimeRoot()) {
    if (!fs.existsSync(runtimeRoot)) return [];
    try {
        const entries = fs.readdirSync(runtimeRoot, { withFileTypes: true });
        return entries
            .filter((e) => e.isDirectory() && !e.name.includes('.tmp-'))
            .map((e) => {
                const dirPath = path.join(runtimeRoot, e.name);
                let stat = null;
                try {
                    stat = fs.statSync(dirPath);
                } catch {
                    // Ignore
                }
                return {
                    name: e.name,
                    path: dirPath,
                    hasGateway: fs.existsSync(path.join(dirPath, 'GxMcp.Gateway.exe')),
                    mtime: stat ? stat.mtime : null
                };
            });
    } catch {
        return [];
    }
}

function getRunningGxMcpProcesses() {
    if (process.platform !== 'win32') return [];
    try {
        // Query CIM Win32_Process for GxMcp processes
        const output = execFileSync('powershell.exe', [
            '-NoProfile',
            '-NonInteractive',
            '-Command',
            `Get-CimInstance Win32_Process -Filter "name like 'GxMcp%'" | Select-Object ProcessId, ExecutablePath, CommandLine | ConvertTo-Json -Compress`
        ], { encoding: 'utf8', timeout: 3000, stdio: ['ignore', 'pipe', 'ignore'] }).trim();

        if (!output) return [];
        let data;
        try {
            data = JSON.parse(output);
        } catch {
            return [];
        }
        const rows = Array.isArray(data) ? data : [data];
        return rows.map((r) => ({
            pid: r.ProcessId,
            exePath: r.ExecutablePath || '',
            commandLine: r.CommandLine || ''
        }));
    } catch {
        return [];
    }
}

function classifyProcessRuntime(exePath, runtimeRoot = resolveDefaultRuntimeRoot()) {
    if (!exePath) return 'unknown';
    const norm = path.resolve(exePath).toLowerCase();
    const normRuntime = path.resolve(runtimeRoot).toLowerCase();
    if (norm.startsWith(normRuntime)) {
        return 'staged';
    }
    if (norm.includes('npm-cache') || norm.includes('_npx') || norm.includes('node_modules')) {
        return 'npx-cache';
    }
    return 'other';
}

module.exports = {
    resolveDefaultRuntimeRoot,
    isCheckoutMode,
    shouldStage,
    computePublishFingerprint,
    verifyManifest,
    cleanOldRuntimes,
    ensureStagedGateway,
    listStagedRuntimes,
    getRunningGxMcpProcesses,
    classifyProcessRuntime
};
