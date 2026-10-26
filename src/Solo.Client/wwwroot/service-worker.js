self.importScripts('./service-worker-assets.js');
const cacheName='solo-app-'+self.assetsManifest.version;
const assets=self.assetsManifest.assets.filter(a=>!a.url.endsWith('.map')&&!a.url.includes('service-worker')).map(a=>a.url);
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
        const asset=await dynamic.match(request);if(asset)return asset;
        const response=await fetch(request);
        if(response.ok&&/^\/api\/(extensions\/[^/]+\/content|themes\/[^/]+\/[^/]+)\//.test(url.pathname))await dynamic.put(request,response.clone());
        return response;
    })());
});
