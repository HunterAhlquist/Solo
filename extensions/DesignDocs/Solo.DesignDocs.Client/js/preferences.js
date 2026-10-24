const uuid=/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
export function viewPreferences(value){
    return {openFolders:[...new Set((Array.isArray(value?.openFolders)?value.openFolders:[]).filter(id=>typeof id==='string'&&uuid.test(id)))],
        noteId:typeof value?.noteId==='string'&&uuid.test(value.noteId)?value.noteId:null,
        fontScale:[75,90,100,110,125,150,175,200].includes(value?.fontScale)?value.fontScale:100,
        marginWidth:[-1,0,5,10,15,20].includes(value?.marginWidth)?value.marginWidth:-1};
}
export class VaultPreferences {
    constructor(read,write){this.read=read;this.write=write;}
    key(vault){if(!uuid.test(vault))throw new Error('Invalid vault id');return 'solo-doc-view-'+vault;}
    load(vault){
        const key=this.key(vault),count=Number(this.read(key+'-count'));
        if(!Number.isInteger(count)||count<1||count>32)return viewPreferences();
        try{return viewPreferences(JSON.parse(decodeURIComponent(Array.from({length:count},(_,i)=>this.read(key+'-'+i)??'').join(''))));}catch{return viewPreferences();}
    }
    clear(vault){
        const key=this.key(vault);
        for(const name of [key+'-count',...Array.from({length:32},(_,i)=>key+'-'+i)])if(this.read(name)!==undefined)this.write(name,null);
    }
}
const browserPreferences=()=>{
    const cookies=new Map(document.cookie.split(';').map(part=>part.trim()).filter(part=>part.includes('=')).map(part=>{const at=part.indexOf('=');return [part.slice(0,at),part.slice(at+1)];}));
    return new VaultPreferences(name=>cookies.get(name),(name,value)=>{
        document.cookie=name+'='+(value??'')+'; Path=/docs; SameSite=Lax; Max-Age=0'+(location.protocol==='https:'?'; Secure':'');cookies.delete(name);
    });
};
// Origin storage isolates desktop instances and avoids cookie eviction.
export function loadVaultPreferences(vault){
    const legacy=browserPreferences(),key=legacy.key(vault);
    let value;
    try{const stored=localStorage.getItem(key);if(stored!==null)value=viewPreferences(JSON.parse(stored));}catch{}
    value??=legacy.load(vault);
    try{localStorage.setItem(key,JSON.stringify(value));legacy.clear(vault);}catch{}
    return value;
}
export function saveVaultPreferences(vault,value){
    const legacy=browserPreferences(),key=legacy.key(vault),clean=viewPreferences(value);
    try{localStorage.setItem(key,JSON.stringify(clean));legacy.clear(vault);}catch{}
}
