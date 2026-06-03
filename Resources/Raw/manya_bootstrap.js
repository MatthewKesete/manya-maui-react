/**
 * MANYA MAUI BOOTSTRAP SHIM
 *
 * Loads in Android WebView and wraps the raw JavaScript bridge methods
 * into Promise-based auth/db/file/kv APIs that match the web app contract.
 */
(function() {
    if (typeof window === 'undefined') return;

    const rawBridge = (() => {
        if (window.ManyaBackend && window.ManyaBackend.__isManyaBootstrap) {
            return null;
        }
        if (window.ManyaBackend) {
            return window.ManyaBackend;
        }
        if (typeof Android !== 'undefined') {
            return Android;
        }
        return null;
    })();

    if (!rawBridge) return;
    if (window.ManyaBackend && window.ManyaBackend.__isManyaBootstrap) return;

    const nativeBridge = rawBridge;
    let _callId = 0;

    function _escapeJsString(value) {
        return value
            .replace(/\\/g, '\\\\')
            .replace(/'/g, "\\'")
            .replace(/\"/g, '\\\"')
            .replace(/\r/g, '\\r')
            .replace(/\n/g, '\\n');
    }

    function _callNative(methodName, ...args) {
        return new Promise((resolve, reject) => {
            const cid = `manya_cb_${++_callId}`;
            
            // Temporary global function to receive the callback from C#
            window[cid] = function(jsonResult) {
                delete window[cid]; // Cleanup
                try {
                    const res = JSON.parse(jsonResult);
                    if (res && res.error) {
                        reject(new Error(res.error));
                    } else {
                        resolve(res);
                    }
                } catch (e) {
                    // Fallback if not valid JSON (e.g. raw string from fileReadText)
                    resolve(jsonResult);
                }
            };

            // Call the C# method on the native bridge object
            try {
                if (!nativeBridge || typeof nativeBridge[methodName] !== 'function') {
                    delete window[cid];
                    reject(new Error(`Native bridge method ${methodName} not found`));
                    return;
                }
                nativeBridge[methodName](...args, cid);
            } catch (err) {
                delete window[cid];
                reject(err);
            }
        });
    }

    // Export the exact contract expected by storageFacade.js
    window.ManyaBackend = {
        // ── AUTH ─────────────────────────────────────────────────────────────
        auth: {
            signIn: (email, password) => 
                _callNative('authSignIn', email, password),
                
            signUp: (email, password, metadata = {}) => 
                _callNative('authSignUp', email, password, JSON.stringify(metadata)),
                
            signOut: () => 
                _callNative('authSignOut'),
                
            getSession: () => 
                _callNative('authGetSession'),
                
            updatePassword: (newPassword) => 
                _callNative('authUpdatePassword', newPassword),
                
            resetPasswordForEmail: (email) => 
                _callNative('authResetPassword', email)
        },

        // ── DATABASE ─────────────────────────────────────────────────────────
        db: {
            get: (table, query) => 
                _callNative('dbGet', table, JSON.stringify(query)),
                
            insert: (table, payload) => 
                _callNative('dbInsert', table, JSON.stringify(payload)),
                
            upsert: (table, payload, options = {}) => 
                _callNative('dbUpsert', table, JSON.stringify(payload), JSON.stringify(options)),
                
            patch: (table, id, patchObj) => 
                _callNative('dbPatch', table, id.toString(), JSON.stringify(patchObj)),
                
            delete: (table, id) => 
                _callNative('dbDelete', table, id.toString()),
                
            deleteAll: (table) => 
                _callNative('dbDeleteAll', table),
                
            bulkUpsert: (table, rows, options = {}) => 
                _callNative('dbBulkUpsert', table, JSON.stringify(rows), JSON.stringify(options))
        },

        // ── FILES ────────────────────────────────────────────────────────────
        files: {
            readJson: (path) => 
                _callNative('fileReadJson', path),
                
            readText: (path) => 
                _callNative('fileReadText', path),
                
            writeJson: (path, data) => 
                _callNative('fileWriteJson', path, JSON.stringify(data)),
                
            // Synchronous wrapper
            getAssetUrl: (path) => {
                if (nativeBridge && typeof nativeBridge.fileGetAssetUrl === 'function') {
                    return nativeBridge.fileGetAssetUrl(path);
                }
                return `file:///android_asset/${path.replace(/^\//, '')}`;
            }
        },

        // ── KV STORE (SYNCHRONOUS) ───────────────────────────────────────────
        kv: {
            get: (key) => {
                if (!nativeBridge || typeof nativeBridge.kvGet !== 'function') return null;
                const val = nativeBridge.kvGet(key);
                return val === 'null' ? null : val;
            },
            set: (key, value) => nativeBridge && nativeBridge.kvSet ? nativeBridge.kvSet(key, value.toString()) : null,
            remove: (key) => nativeBridge && nativeBridge.kvRemove ? nativeBridge.kvRemove(key) : null,
            clear: () => nativeBridge && nativeBridge.kvClear ? nativeBridge.kvClear() : null
        }
    };

    console.log('[Manya] 🌉 Native ManyaBackend initialized');
})();
