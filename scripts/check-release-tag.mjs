import {readFileSync} from 'node:fs';
const version=readFileSync('Directory.Build.props','utf8').match(/<Version>([^<]+)<\/Version>/)[1];
if(process.argv[2]!==`v${version}`)throw new Error(`The release tag must be v${version}`);
