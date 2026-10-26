self.importScripts('./service-worker-assets.js');
const cacheName='solo-app-'+self.assetsManifest.version;
const assets=self.assetsManifest.assets.filter(a=>!a.url.endsWith('.map')&&!a.url.includes('service-worker')).map(a=>a.url);
const themeAsset=/^\/api\/themes\/([^/]+)\/[^/]+\/(.+)$/;
function assetKey(url){const theme=url.pathname.match(themeAsset);return theme?`/api/themes/${theme[1]}/${theme[2]}`:url.pathname;}
async function trimVersions(cache,request){
    // Keep one preceding version for tabs that still run the previous extension.
    const url=new URL(request.url),keys=await cache.keys(themeAsset.test(url.pathname)?undefined:request,{ignoreSearch:true});
    const older=keys.filter(key=>{const previous=new URL(key.url);return key.url!==request.url&&assetKey(previous)===assetKey(url)&&(previous.searchParams.has('v')||themeAsset.test(previous.pathname));});
    for(const key of older.slice(0,-1))await cache.delete(key);
}
self.addEventListener('install',event=>event.waitUntil((async()=>{
    const cache=await caches.open(cacheName);
    await cache.addAll(assets.map(url=>new Request(url,{cache:'reload'})));
})()));
self.addEventListener('activate',event=>event.waitUntil((async()=>{
    for(const name of await caches.keys())if(name.startsWith('solo-app-')&&name!==cacheName)await caches.delete(name);
    await self.clients.claim();
})()));
self.addEventListener('fetch',event=>{
    const request=event.request,url=new URL(request.url);
    if(request.method!=='GET'||url.origin!==self.location.origin||url.pathname.startsWith('/api/')&&!/^\/api\/(extensions\/[^/]+\/content|themes\/[^/]+\/[^/]+)\//.test(url.pathname)||url.pathname.startsWith('/hubs/'))return;
    event.respondWith((async()=>{
        const cache=await caches.open(cacheName);
        const key=request.mode==='navigate'?'index.html':request;
        const cached=await cache.match(key);if(cached)return cached;
        const dynamic=await caches.open('solo-offline-assets');
        const asset=await dynamic.match(request),versioned=url.searchParams.has('v')||themeAsset.test(url.pathname);if(asset&&versioned){await trimVersions(dynamic,request);return asset;}
        let response;
        try{response=await fetch(request,versioned?undefined:{cache:'no-cache'});}catch(error){if(asset)return asset;throw error;}
        if(response.status>=500&&asset)return asset;
        if(response.ok&&/^\/api\/(extensions\/[^/]+\/content|themes\/[^/]+\/[^/]+)\//.test(url.pathname)){
            await dynamic.put(request,response.clone());
            if(versioned)await trimVersions(dynamic,request);
        }
        return response;
    })());
});
