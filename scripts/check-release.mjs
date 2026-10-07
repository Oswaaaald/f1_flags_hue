import assert from 'node:assert/strict';
import {readFile,readdir,stat,writeFile} from 'node:fs/promises';
import {createHash} from 'node:crypto';
import path from 'node:path';
const expected=(await readFile('Directory.Build.props','utf8')).match(/<Version>([^<]+)<\/Version>/)[1];
const [target]=process.argv.slice(2);
const roots=target?[target]:['osx-arm64','osx-x64','win-x64','win-arm64','linux-x64','linux-arm64'];
for(const runtime of roots){
 const folder=`artifacts/${runtime}/service`;
 const deps=JSON.parse(await readFile(`${folder}/f1-hue.deps.json`,'utf8'));
 assert.ok(deps.libraries[`f1-hue/${expected}`],`${runtime} must contain ${expected}`);
 const walk=async dir=>{for(const file of await readdir(dir)){const full=path.join(dir,file);if((await stat(full)).isDirectory())await walk(full);else assert.ok(!/^(config\.yml|bridge\.enc|vault\.key|desktop-launch\.key|setup-code\.txt|.*\.sqlite3(?:-wal|-shm)?)$/.test(file),`Private file in release: ${full}`);}};
 await walk(folder);
 console.log(`PASS ${runtime}: version ${expected}, no private profile files`);
}
if(!target){
 const rows=[];
 for(const name of (await readdir('artifacts')).filter(n=>/\.(zip|tar\.gz|exe)$/.test(n)).sort()){
  const bytes=await readFile(path.join('artifacts',name));rows.push(`${createHash('sha256').update(bytes).digest('hex')}  ${name}`);
 }
 await writeFile('artifacts/SHA256SUMS',rows.join('\n')+'\n');
 console.log('SHA256SUMS generated.');
}
