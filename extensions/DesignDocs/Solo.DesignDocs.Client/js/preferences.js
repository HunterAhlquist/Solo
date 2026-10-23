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
    save(vault,value){
        const key=this.key(vault),encoded=encodeURIComponent(JSON.stringify(viewPreferences(value))),count=Math.ceil(encoded.length/3000),old=Number(this.read(key+'-count'))||0;
        // Split large trees so a cookie never silently exceeds the browser's size limit.
        if(count>32)throw new Error('Too many expanded folders to remember');
        for(let i=0;i<count;i++)this.write(key+'-'+i,encoded.slice(i*3000,(i+1)*3000));
        this.write(key+'-count',String(count));for(let i=count;i<Math.min(old,32);i++)this.write(key+'-'+i,null);
    }
}
const browserPreferences=()=>new VaultPreferences(
    name=>document.cookie.split(';').map(part=>part.trim()).find(part=>part.startsWith(name+'='))?.slice(name.length+1),
    (name,value)=>{document.cookie=name+'='+(value??'')+'; Path=/docs; SameSite=Lax; Max-Age='+(value===null?0:31536000)+(location.protocol==='https:'?'; Secure':'');});
export const loadVaultPreferences=vault=>browserPreferences().load(vault);
export const saveVaultPreferences=(vault,value)=>browserPreferences().save(vault,value);
