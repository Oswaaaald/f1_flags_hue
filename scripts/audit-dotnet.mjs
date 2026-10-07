import {spawnSync} from 'node:child_process';
const result=spawnSync(process.argv[2]??'dotnet',['list','F1Hue.slnx','package','--vulnerable','--include-transitive','--format','json'],{encoding:'utf8'});
if(result.status!==0){process.stderr.write(result.stderr||result.stdout);process.exit(result.status??1);}
const report=JSON.parse(result.stdout);
let count=0;
function visit(value){if(!value||typeof value!=='object')return;for(const [key,child]of Object.entries(value)){if(key==='vulnerabilities'&&Array.isArray(child))count+=child.length;else visit(child);}}
visit(report);
console.log(`NuGet: ${count} known vulnerable dependency entries.`);
if(count){process.stdout.write(result.stdout);process.exit(1);}
