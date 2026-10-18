import { build } from 'esbuild';
await build({entryPoints:['src/app.mjs'],outfile:'public/app.js',bundle:true,minify:true,format:'esm',target:['es2022'],legalComments:'eof'});
console.log('Dashboard bundle built.');
