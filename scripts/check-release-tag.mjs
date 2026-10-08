import {readFileSync} from 'node:fs';
const version=readFileSync('Directory.Build.props','utf8').match(/<Version>([^<]+)<\/Version>/)[1];
for(const file of ['apps/web/package.json','apps/web/package-lock.json']) {
  if(JSON.parse(readFileSync(file,'utf8')).version!==version)throw new Error(`${file} must use ${version}`);
}
const preview=version.match(/^2\.0\.0-preview\.(\d+)$/);
if(preview&&!readFileSync('deploy/macos/Info.plist','utf8').includes(`<string>${20000+Number(preview[1])}</string>`))
  throw new Error('macOS bundle build number differs from the release version');
if(!process.argv[2])console.log(`v${version}`);
else if(process.argv[2]!==`v${version}`)throw new Error(`The release tag must be v${version}`);
