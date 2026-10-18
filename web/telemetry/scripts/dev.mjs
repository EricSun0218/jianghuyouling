import { createServer } from 'node:http';
import { readFile } from 'node:fs/promises';
import { createHandler } from '../lib/handler.mjs';
const handler=createHandler();
const assets=new Map([['/',['public/index.html','text/html; charset=utf-8']],['/styles.css',['public/styles.css','text/css']],['/app.js',['public/app.js','text/javascript']],['/third-party-licenses.txt',['public/third-party-licenses.txt','text/plain; charset=utf-8']]]);
createServer(async(req,res)=>{
  try {
    const url=new URL(req.url,'http://127.0.0.1:18793');
    if(url.pathname.startsWith('/api/')) {
      const request=new Request(url,{method:req.method,headers:req.headers,...(!['GET','HEAD'].includes(req.method)?{body:req,duplex:'half'}:{})});
      const response=await handler(request);res.writeHead(response.status,Object.fromEntries(response.headers));res.end(Buffer.from(await response.arrayBuffer()));return;
    }
    const entry=assets.get(url.pathname);if(!entry){res.writeHead(404).end();return;}
    res.writeHead(200,{'Content-Type':entry[1]});res.end(await readFile(entry[0]));
  } catch {res.writeHead(500).end('Local development error');}
}).listen(18793,'127.0.0.1',()=>console.log('Dashboard: http://127.0.0.1:18793'));
