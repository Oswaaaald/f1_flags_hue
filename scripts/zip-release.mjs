// Use the OS archive tool; no application runtime dependency is added.
import {spawnSync} from 'node:child_process';
import {rmSync} from 'node:fs';
import path from 'node:path';
const [source,destination]=process.argv.slice(2).map(value=>path.resolve(value));
if(!source||!destination)throw new Error('Usage: zip-release.mjs source destination');
rmSync(destination,{force:true});
const result=spawnSync('zip',['-qr',destination,'.'],{cwd:source,stdio:'inherit'});
if(result.error)throw result.error;
process.exit(result.status??1);
