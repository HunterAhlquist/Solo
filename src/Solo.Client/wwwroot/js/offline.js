(() => {
    const ready=new Promise((resolve,reject)=>{
        const request=indexedDB.open('solo-offline-host-v1',1);
        request.onupgradeneeded=()=>request.result.createObjectStore('entries',{keyPath:'key'});
        request.onsuccess=()=>resolve(request.result);request.onerror=()=>reject(request.error);
    });
    const scope=async()=>String(await window.solo.desktop.instanceUrl()||location.origin).replace(/\/$/,'');
    async function read(key){const db=await ready;return new Promise((resolve,reject)=>{const request=db.transaction('entries').objectStore('entries').get(key);request.onsuccess=()=>resolve(request.result?.value??null);request.onerror=()=>reject(request.error);});}
    async function write(key,value){const db=await ready;return new Promise((resolve,reject)=>{const tx=db.transaction('entries','readwrite');tx.objectStore('entries').put({key,value});tx.oncomplete=()=>resolve();tx.onerror=()=>reject(tx.error);});}
    let unreachable=!navigator.onLine,generation=0,reconnecting;
    const publicPath=path=>path==='api/config/auth'||path==='api/themes/active';
    const accountKey=async()=>await scope()+'|account';
    let currentAccount;
    const account=async()=>{if(currentAccount===undefined)currentAccount=await read(await accountKey());return currentAccount;};
    const guards=new Set();
    window.solo.offline={
        guard:callback=>{guards.add(callback);return ()=>guards.delete(callback);},
        flush:async()=>{const results=await Promise.allSettled([...guards].map(callback=>callback()));return results.every(r=>r.status==='fulfilled'&&r.value!==false);},
        epoch:()=>generation,
        isOffline:()=>unreachable||!navigator.onLine,
        network:value=>{unreachable=!value;},
        reconnect:()=>{
            if(!navigator.onLine)return Promise.resolve(false);
            return reconnecting??= (async()=>{
                const controller=new AbortController(),timer=setTimeout(()=>controller.abort(),5000);
                try{const response=await fetch('/api/config/auth',{cache:'no-store',signal:controller.signal});unreachable=!response.ok;return response.ok;}
                catch{unreachable=true;return false;}
                finally{clearTimeout(timer);reconnecting=null;}
            })();
        },
        profile:async()=>{const current=await account();return current?JSON.stringify(current):null;},
        lock:async expected=>{if(expected===undefined||expected===generation){generation++;currentAccount=null;await write(await accountKey(),null);}},
        credential:async email=>{const current=await account();if(current?.email?.toLowerCase()!==email?.toLowerCase()){generation++;currentAccount=null;await write(await accountKey(),null);}},
        async get(path){const base=await scope();const user=publicPath(path)?'public':(await account())?.id;if(!user)return null;return read(base+'|'+user+'|'+path);},
        async put(path,data,expected=generation){
            if(expected!==generation)return;
            const base=await scope();
            if(path==='api/users/me'){
                const bytes=Uint8Array.from(atob(data.data),c=>c.charCodeAt(0));const profile=JSON.parse(new TextDecoder().decode(bytes));
                if(expected!==generation)return;currentAccount=profile;await write(await accountKey(),profile);
            }
            const user=publicPath(path)?'public':(await account())?.id;if(user&&expected===generation)await write(base+'|'+user+'|'+path,data);
        },
        async prepare(urls=[]){
            const dynamic=await caches.open('solo-offline-assets');
            await dynamic.addAll(urls);
            if(!('serviceWorker' in navigator))throw new Error('This browser cannot cache the app for offline use.');
            const registration=await navigator.serviceWorker.register('/service-worker.js',{updateViaCache:'none'});
            if(!registration.active){await Promise.race([navigator.serviceWorker.ready,new Promise((_,reject)=>setTimeout(()=>reject(new Error('The app download did not finish. Try again while connected.')),120000))]);}
            for(const entry of performance.getEntriesByType('resource')){
                const url=new URL(entry.name);if(url.origin===location.origin&&/^\/api\/themes\/[^/]+\/[^/]+\//.test(url.pathname))await dynamic.add(url.toString());
            }
            return true;
        },
    };
    navigator.serviceWorker?.getRegistration().then(registration=>registration?.update()).catch(()=>{});
    window.addEventListener('online',()=>unreachable=false);
    window.addEventListener('offline',()=>unreachable=true);
})();
