'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('fs');
const path = require('path');
const os = require('os');
const crypto = require('crypto');

const {
    resolveDefaultRuntimeRoot,
    shouldStage,
    ensureStagedGateway,
    cleanOldRuntimes,
    verifyManifest,
    classifyProcessRuntime
} = require('./runtime-stager');

function sha256(content) {
    return crypto.createHash('sha256').update(content).digest('hex');
}

test('runtime-stager: resolveDefaultRuntimeRoot uses GENEXUS_MCP_RUNTIME_DIR or LocalAppData', (t) => {
    const orig = process.env.GENEXUS_MCP_RUNTIME_DIR;
    t.after(() => {
        if (orig !== undefined) process.env.GENEXUS_MCP_RUNTIME_DIR = orig;
        else delete process.env.GENEXUS_MCP_RUNTIME_DIR;
    });
    process.env.GENEXUS_MCP_RUNTIME_DIR = 'C:\\custom\\runtime';
    assert.equal(resolveDefaultRuntimeRoot(), 'C:\\custom\\runtime');
});

test('runtime-stager: verifyManifest succeeds on valid files', () => {
    const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'gx-verify-'));
    try {
        const file = path.join(tmp, 'test.txt');
        fs.writeFileSync(file, 'hello');
        const manifest = path.join(tmp, 'manifest.json');
        fs.writeFileSync(manifest, JSON.stringify({
            artifacts: [{ path: 'test.txt', size: 5, sha256: sha256('hello') }]
        }));
        assert.doesNotThrow(() => verifyManifest(tmp, manifest));
    } finally {
        try { fs.rmSync(tmp, { recursive: true, force: true }); } catch { /* ignore */ }
    }
});

test('runtime-stager: shouldStage respects env overrides and checkout markers', (t) => {
    const origEnv = { ...process.env };
    t.after(() => {
        process.env = origEnv;
    });

    const tmpPkg = fs.mkdtempSync(path.join(os.tmpdir(), 'gx-pkg-'));
    t.after(() => {
        try { fs.rmSync(tmpPkg, { recursive: true, force: true }); } catch { /* ignore */ }
    });

    // Case 1: GENEXUS_MCP_GATEWAY_EXE set -> false
    process.env.GENEXUS_MCP_GATEWAY_EXE = 'C:\\custom\\GxMcp.Gateway.exe';
    delete process.env.GENEXUS_MCP_NO_STAGING;
    delete process.env.GENEXUS_MCP_FORCE_STAGING;
    assert.equal(shouldStage(tmpPkg), false);

    // Case 2: GENEXUS_MCP_NO_STAGING === '1' -> false
    delete process.env.GENEXUS_MCP_GATEWAY_EXE;
    process.env.GENEXUS_MCP_NO_STAGING = '1';
    assert.equal(shouldStage(tmpPkg), false);

    // Case 3: In packaged mode (no .git) -> true
    delete process.env.GENEXUS_MCP_NO_STAGING;
    assert.equal(shouldStage(tmpPkg), true);

    // Case 4: With .git marker -> false
    fs.mkdirSync(path.join(tmpPkg, '.git'));
    assert.equal(shouldStage(tmpPkg), false);

    // Case 5: With .git marker but GENEXUS_MCP_FORCE_STAGING === '1' -> true
    process.env.GENEXUS_MCP_FORCE_STAGING = '1';
    assert.equal(shouldStage(tmpPkg), true);
});

test('runtime-stager: ensureStagedGateway stages publish folder, verifies manifest and reuses staged runtime', (t) => {
    const tmpRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'gx-stage-test-'));
    t.after(() => {
        try { fs.rmSync(tmpRoot, { recursive: true, force: true }); } catch { /* ignore */ }
    });

    const fakePkg = path.join(tmpRoot, 'package');
    const fakePublish = path.join(fakePkg, 'publish');
    fs.mkdirSync(fakePublish, { recursive: true });

    fs.writeFileSync(path.join(fakePkg, 'package.json'), JSON.stringify({ version: '3.9.0' }));
    const fakeExeContent = 'fake gateway binary';
    fs.writeFileSync(path.join(fakePublish, 'GxMcp.Gateway.exe'), fakeExeContent);

    const manifestContent = JSON.stringify({
        schemaVersion: 'gxmcp-release-manifest/1',
        artifacts: [
            {
                path: 'GxMcp.Gateway.exe',
                size: Buffer.byteLength(fakeExeContent),
                sha256: sha256(fakeExeContent)
            }
        ]
    });
    fs.writeFileSync(path.join(fakePublish, 'gxmcp-manifest.json'), manifestContent);

    const runtimeRoot = path.join(tmpRoot, 'runtime');

    // First run: stages fresh
    const result1 = ensureStagedGateway({
        packageRoot: fakePkg,
        publishDir: fakePublish,
        runtimeRoot,
        forceStaging: true
    });

    assert.equal(result1.staged, true);
    assert.equal(result1.fresh, true);
    assert.equal(fs.existsSync(result1.gatewayExePath), true);
    assert.equal(fs.readFileSync(result1.gatewayExePath, 'utf8'), fakeExeContent);

    // Second run: reuses already staged
    const result2 = ensureStagedGateway({
        packageRoot: fakePkg,
        publishDir: fakePublish,
        runtimeRoot,
        forceStaging: true
    });

    assert.equal(result2.staged, true);
    assert.equal(result2.fresh, false);
    assert.equal(result2.gatewayExePath, result1.gatewayExePath);
});

test('runtime-stager: ensureStagedGateway fails closed and cleans tmp on manifest corruption', (t) => {
    const tmpRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'gx-corrupt-test-'));
    t.after(() => {
        try { fs.rmSync(tmpRoot, { recursive: true, force: true }); } catch { /* ignore */ }
    });

    const fakePkg = path.join(tmpRoot, 'package');
    const fakePublish = path.join(fakePkg, 'publish');
    fs.mkdirSync(fakePublish, { recursive: true });

    fs.writeFileSync(path.join(fakePkg, 'package.json'), JSON.stringify({ version: '3.9.0' }));
    const fakeExeContent = 'tampered gateway binary';
    fs.writeFileSync(path.join(fakePublish, 'GxMcp.Gateway.exe'), fakeExeContent);

    const manifestContent = JSON.stringify({
        schemaVersion: 'gxmcp-release-manifest/1',
        artifacts: [
            {
                path: 'GxMcp.Gateway.exe',
                size: 9999, // mismatched size
                sha256: 'deadbeef'
            }
        ]
    });
    fs.writeFileSync(path.join(fakePublish, 'gxmcp-manifest.json'), manifestContent);

    const runtimeRoot = path.join(tmpRoot, 'runtime');

    assert.throws(() => {
        ensureStagedGateway({
            packageRoot: fakePkg,
            publishDir: fakePublish,
            runtimeRoot,
            forceStaging: true
        });
    }, /Manifest verification failed/);

    // Assert that target dir was not created and no tmp dir left behind
    assert.equal(fs.readdirSync(runtimeRoot).length, 0);
});

test('runtime-stager: cleanOldRuntimes retains current version plus keepCount newest versions', (t) => {
    const tmpRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'gx-gc-test-'));
    t.after(() => {
        try { fs.rmSync(tmpRoot, { recursive: true, force: true }); } catch { /* ignore */ }
    });

    const v1 = path.join(tmpRoot, '3.6.0-aaaa1111');
    const v2 = path.join(tmpRoot, '3.7.0-bbbb2222');
    const v3 = path.join(tmpRoot, '3.8.0-cccc3333');
    const v4 = path.join(tmpRoot, '3.9.0-dddd4444');

    fs.mkdirSync(v1);
    fs.utimesSync(v1, 1000, 1000);
    fs.mkdirSync(v2);
    fs.utimesSync(v2, 2000, 2000);
    fs.mkdirSync(v3);
    fs.utimesSync(v3, 3000, 3000);
    fs.mkdirSync(v4);
    fs.utimesSync(v4, 4000, 4000);

    // Current is v4. Keep count = 2 -> keep v4, v3, v2. v1 should be deleted.
    cleanOldRuntimes(tmpRoot, v4, 2);

    assert.equal(fs.existsSync(v4), true, 'current version must be kept');
    assert.equal(fs.existsSync(v3), true, 'newest previous version must be kept');
    assert.equal(fs.existsSync(v2), true, 'second previous version must be kept');
    assert.equal(fs.existsSync(v1), false, 'oldest version beyond keepCount must be deleted');
});

test('runtime-stager: classifyProcessRuntime identifies staged vs npx-cache', () => {
    const runtimeRoot = 'C:\\Users\\user\\AppData\\Local\\GenexusMCP\\runtime';
    assert.equal(
        classifyProcessRuntime('C:\\Users\\user\\AppData\\Local\\GenexusMCP\\runtime\\3.8.0-abcd\\GxMcp.Gateway.exe', runtimeRoot),
        'staged'
    );
    assert.equal(
        classifyProcessRuntime('C:\\Users\\user\\AppData\\Local\\npm-cache\\_npx\\12345\\node_modules\\genexus-mcp\\publish\\GxMcp.Gateway.exe', runtimeRoot),
        'npx-cache'
    );
    assert.equal(
        classifyProcessRuntime('C:\\Projetos\\Genexus18MCP\\publish\\GxMcp.Gateway.exe', runtimeRoot),
        'other'
    );
});
