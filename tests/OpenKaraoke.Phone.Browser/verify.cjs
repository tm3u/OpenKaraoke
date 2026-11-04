const fs=require('node:fs'),path=require('node:path'),vm=require('node:vm');
const root=path.resolve(process.argv[2]),infoPath=path.resolve(process.argv[3]),info=JSON.parse(fs.readFileSync(infoPath,'utf8')),folder=path.dirname(infoPath),checks=[];
function check(name,passed,evidence){checks.push({Name:name,Passed:!!passed,Evidence:evidence});console.log(`${passed?'PASS':'FAIL'} ${name}`);if(!passed)throw new Error(name);}
function wave(file){const rate=16000,length=rate*4,data=Buffer.alloc(44+length*2);data.write('RIFF',0);data.writeUInt32LE(data.length-8,4);data.write('WAVEfmt ',8);data.writeUInt32LE(16,16);data.writeUInt16LE(1,20);data.writeUInt16LE(1,22);data.writeUInt32LE(rate,24);data.writeUInt32LE(rate*2,28);data.writeUInt16LE(2,32);data.writeUInt16LE(16,34);data.write('data',36);data.writeUInt32LE(length*2,40);for(let i=0;i<length;i++)data.writeInt16LE(Math.round(Math.sin(2*Math.PI*440*i/rate)*8000),44+i*2);fs.writeFileSync(file,data);}
async function main(){
  const code=fs.readFileSync(path.join(root,'src/OpenKaraoke.Phone/Web/audio-worklet.js'),'utf8');
  for(const rate of [16000,44100,48000]){
    let Constructor,total=0,packets=0;const values=[];
    const sandbox={sampleRate:rate,currentFrame:0,AudioWorkletProcessor:class{constructor(){this.port={postMessage:data=>{total+=data.pcm.byteLength/2;packets++;values.push(...new Int16Array(data.pcm));}};}},registerProcessor:(_,type)=>Constructor=type};
    vm.runInNewContext(code,sandbox);const processor=new Constructor({processorOptions:{enhanced:true,gainDb:18}});
    for(let offset=0;offset<rate;offset+=128){const input=new Float32Array(Math.min(128,rate-offset)).fill(.25),out=new Float32Array(input.length).fill(1);sandbox.currentFrame=offset;processor.process([[input]],[[out]]);if(out.some(v=>v!==0))throw new Error('手機發生本機回放');}
    check(`${rate} Hz 每 10ms 原生分段，不重取樣或本機回放`,total+processor.used===rate&&packets===100&&processor.packet.length===rate/100,{Packets:packets});
    check(`${rate} Hz 忽略舊增益選項，保留原始 PCM 振幅`,values.every(value=>value===8192));
  }
  const {chromium}=require(path.join(root,'.tools/phone-browser/package'));const audio=path.join(folder,'input-440.wav');wave(audio);
  const browser=await chromium.launch({executablePath:'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',headless:true,args:['--use-fake-device-for-media-stream','--use-fake-ui-for-media-stream','--use-file-for-fake-audio-capture='+audio]});
  const contexts=[],errors=[];let active;
  const state=async context=>(await context.request.get(info.SecureUrl+'/api/state')).json();
  async function open(mode='success',paired=true){
    const context=await browser.newContext({viewport:{width:390,height:844},isMobile:true,hasTouch:true,ignoreHTTPSErrors:true});contexts.push(context);
    await context.addInitScript(mode=>{
      window.captureRequests=[];window.captureTracks=[];window.micSockets=[];window.audioContexts=[];window.binarySent=0;window.wakeRequests=0;window.wakeHandles=[];window.wakeMode=mode;window.pageVisibility='visible';window.timeShift=0;const nativeNow=performance.now.bind(performance);performance.now=()=>nativeNow()+window.timeShift;
      Object.defineProperty(document,'visibilityState',{configurable:true,get:()=>window.pageVisibility});
      window.setVisibility=value=>{window.pageVisibility=value;document.dispatchEvent(new Event('visibilitychange'));};
      class ScreenLock extends EventTarget{constructor(){super();this.released=false;}async release(){if(this.released)return;this.released=true;this.dispatchEvent(new Event('release'));}}
      if(mode!=='native')Object.defineProperty(navigator,'wakeLock',{configurable:true,value:mode==='unsupported'?undefined:{request:async type=>{
        if(type!=='screen')throw new Error('錯誤的常亮類型');window.wakeRequests++;
        if(window.wakeMode==='reject')throw new DOMException('Power saving','NotAllowedError');
        const held=new ScreenLock();window.wakeHandles.push(held);
        if(window.wakeMode==='hold')await new Promise(resolve=>window.releaseWakeRequest=resolve);
        return held;
      }}});
      if(mode==='native'&&navigator.wakeLock?.request){const request=navigator.wakeLock.request.bind(navigator.wakeLock);navigator.wakeLock.request=async type=>{const held=await request(type);window.nativeWakeHandle=held;return held;};}
      const nativeCapture=navigator.mediaDevices.getUserMedia.bind(navigator.mediaDevices);
      navigator.mediaDevices.getUserMedia=async options=>{window.captureRequests.push(options);if(window.holdCapture)await new Promise(resolve=>window.releaseCapture=resolve);const result=await nativeCapture(options);window.captureTracks.push(...result.getAudioTracks());return result;};
      const NativeContext=window.AudioContext;window.AudioContext=function(...args){const value=new NativeContext(...args);window.audioContexts.push(value);return value;};window.AudioContext.prototype=NativeContext.prototype;
      const NativeSocket=window.WebSocket;window.WebSocket=class extends NativeSocket{constructor(...args){super(...args);window.micSockets.push(this);this.addEventListener("message",event=>{try{const value=JSON.parse(event.data);if(value.type==="pong")window.lastServerTime=value.serverTime;}catch{}});}get bufferedAmount(){return window.fakeBufferedAmount??super.bufferedAmount;}send(data){if(data instanceof ArrayBuffer){window.binarySent++;window.lastTimestamp=new DataView(data).getFloat64(8,true);}return super.send(data);}};
    },mode);
    const page=await context.newPage();active=page;page.on('pageerror',error=>errors.push(error.message));
    await page.goto(paired?info.MicrophoneUrl:info.SecureUrl);if(paired)await page.waitForFunction(()=>!document.getElementById('toggle').disabled);
    return{page,context};
  }
  async function start(item){await item.page.locator('#toggle').click();await item.page.waitForFunction(()=>document.getElementById('toggle').dataset.state==='enabled');}
  async function stop(item){await item.page.locator('#toggle').click();await item.page.waitForFunction(()=>document.getElementById('toggle').dataset.state==='disabled');}
  try{
    const first=await open();
    check('收音頁的專案標誌正確載入',await first.page.locator('header img').evaluate(image=>image.getAttribute('src')==='/logo.svg'&&image.complete&&image.naturalWidth>0));
    check('手機只保留麥克風開關，沒有增益、音量滑桿及診斷',await first.page.locator('button').count()===1&&await first.page.locator('input,details,#enhance,#automatic,#diagnostics').count()===0);
    await first.page.evaluate(()=>window.holdCapture=true);await first.page.locator('#toggle').click();await first.page.waitForFunction(()=>window.captureRequests.length>0);
    check('建立收音連線時鎖住重複操作，尚未要求螢幕常亮',await first.page.locator('#toggle').isDisabled()&&await first.page.locator('#toggle').getAttribute('data-state')==='changing'&&await first.page.evaluate(()=>wakeRequests===0));
    await first.page.screenshot({path:path.join(folder,'phone-mobile-enabling.png')});
    await first.page.evaluate(()=>window.releaseCapture());await first.page.waitForFunction(()=>document.getElementById('toggle').dataset.state==='enabled'&&document.getElementById('screen-status').textContent.includes('螢幕常亮中'));
    await first.page.waitForFunction(()=>binarySent>5);
    check('收音固定停用自動增益、迴音消除與降噪',await first.page.evaluate(()=>captureRequests.every(({audio:a})=>a.channelCount===1&&a.autoGainControl===false&&a.echoCancellation===false&&a.noiseSuppression===false)));
    check('實際加密 WebSocket 傳送原始音訊且服務上限為兩支',(await state(first.context)).connected&&(await state(first.context)).limit===2);
    check('收音啟用後才要求並持有螢幕常亮',await first.page.evaluate(()=>wakeRequests===1&&wakeHandles.length===1&&!wakeHandles[0].released));
    await first.page.screenshot({path:path.join(folder,'phone-mobile-active.png')});
    const duplicate=await first.context.newPage();await duplicate.goto(info.MicrophoneUrl);await duplicate.waitForFunction(()=>!document.getElementById('toggle').disabled);await duplicate.locator('#toggle').click();await duplicate.locator('#error').waitFor({state:'visible'});
    check('同一手機其他分頁正在收音時說明原因，不另外取得裝置',await duplicate.locator('#error').innerText().then(text=>text.includes('其他分頁'))&&await duplicate.evaluate(()=>captureRequests.length===0)&&(await state(first.context)).connected);await duplicate.close();
    const second=await open();await start(second);check('兩支手機同時收音且編號不同',(await state(second.context)).count===2&&(await state(first.context)).slot!==(await state(second.context)).slot);
    const third=await open();await third.page.locator('#toggle').click();await third.page.locator('#error').waitFor({state:'visible'});
    check('第三支被拒絕，不取得收音資源並維持兩支既有連線',await third.page.locator('#toggle').getAttribute('data-state')==='disabled'&&await third.page.evaluate(()=>captureRequests.length===0&&captureTracks.every(track=>track.readyState==='ended')&&wakeRequests===0)&&(await state(first.context)).count===2&&(await state(second.context)).connected);
    await stop(second);
    await first.page.evaluate(()=>window.setVisibility('hidden'));await first.page.waitForFunction(()=>wakeHandles[0].released);
    check('頁面隱藏時釋放螢幕常亮，不額外重新開啟收音',await first.page.evaluate(()=>wakeRequests===1&&captureRequests.length===1));
    await first.page.evaluate(()=>window.setVisibility('visible'));await first.page.waitForFunction(()=>wakeRequests===2&&wakeHandles.length===2&&!wakeHandles[1].released);
    check('切回前景重新取得螢幕常亮並沿用現有連線',(await state(first.context)).connected&&await first.page.evaluate(()=>captureRequests.length===1));
    await first.page.evaluate(()=>{window.setVisibility('hidden');window.timeShift=60000;window.setVisibility('visible');});await first.page.waitForFunction(()=>document.getElementById('toggle').dataset.state==='enabled'&&Math.abs(lastTimestamp-lastServerTime)<500);
    check('背景恢復時重建時鐘同步，跨分鐘時差不造成持續丟包',await first.page.evaluate(()=>Math.abs(lastTimestamp-lastServerTime)<500));
    await first.page.evaluate(()=>window.wakeHandles.at(-1).release());await first.page.waitForFunction(()=>document.getElementById('screen-status').textContent.includes('系統已解除'));const requestCount=await first.page.evaluate(()=>wakeRequests);await first.page.waitForTimeout(120);
    check('系統解除常亮時說明原因，不反覆忙碌重試',await first.page.evaluate(count=>wakeRequests===count,requestCount));
    await first.page.evaluate(()=>{window.setVisibility('hidden');window.setVisibility('visible');});await first.page.waitForFunction(()=>!wakeHandles.at(-1).released&&document.getElementById('screen-status').textContent.includes('常亮中'));
    let sent=await first.page.evaluate(()=>binarySent);await first.page.evaluate(()=>window.fakeBufferedAmount=100000);await first.page.waitForTimeout(160);
    check('待送佇列超標略過音訊，不追播積壓或切斷連線',await first.page.evaluate(count=>binarySent===count,sent)&&(await state(first.context)).connected);
    await first.page.evaluate(()=>window.fakeBufferedAmount=0);await first.page.waitForFunction(count=>binarySent>count,sent);
    await first.page.evaluate(async()=>{window.setVisibility('hidden');await audioContexts.at(-1).suspend();window.setVisibility('visible');});await first.page.waitForFunction(()=>audioContexts.at(-1).state==='running'&&document.getElementById('toggle').dataset.state==='enabled');
    check('切回頁面恢復被暫停的 AudioContext，收音狀態依實際結果顯示',(await state(first.context)).connected);
    await first.page.evaluate(()=>audioContexts.at(-1).suspend());await first.page.waitForFunction(()=>audioContexts.at(-1).state==='running'&&document.getElementById('toggle').dataset.state==='enabled');
    check('頁面仍在前景時音訊被暫停，也會依實際恢復結果更新狀態',(await state(first.context)).connected);
    await stop(first);await first.page.waitForFunction(()=>wakeHandles.every(held=>held.released));
    check('停用麥克風釋放全部螢幕常亮與收音裝置',await first.page.evaluate(()=>captureTracks.every(track=>track.readyState==='ended')&&document.getElementById('screen-status').hidden));
    await first.page.screenshot({path:path.join(folder,'phone-mobile.png')});
    const denied=await open('reject');await start(denied);await denied.page.waitForFunction(()=>document.getElementById('screen-status').textContent.includes('無法保持'));
    check('常亮被系統拒絕時保留收音並顯示替代步驟',(await state(denied.context)).connected&&await denied.page.locator('#screen-status').innerText().then(text=>text.includes('低耗電模式')&&text.includes('永不')));
    await denied.page.screenshot({path:path.join(folder,'phone-mobile-wake-denied.png')});await stop(denied);
    const unsupported=await open('unsupported');await start(unsupported);await unsupported.page.waitForFunction(()=>document.getElementById('screen-status').textContent.includes('不支援'));
    check('不支援常亮的瀏覽器仍可收音並提供自動鎖定說明',(await state(unsupported.context)).connected&&await unsupported.page.evaluate(()=>wakeRequests===0));await stop(unsupported);
    const pending=await open('hold');await start(pending);await pending.page.waitForFunction(()=>wakeHandles.length===1);await stop(pending);
    await pending.page.evaluate(()=>window.wakeMode='success');await start(pending);await pending.page.waitForFunction(()=>wakeHandles.length===2&&!wakeHandles[1].released);await pending.page.evaluate(()=>window.releaseWakeRequest());await pending.page.waitForFunction(()=>wakeHandles[0].released);
    check('停用後晚到的常亮請求會釋放，不影響新一輪常亮',await pending.page.evaluate(()=>!wakeHandles[1].released&&document.getElementById('toggle').dataset.state==='enabled'));
    await pending.page.evaluate(async()=>{window.setVisibility('hidden');const current=audioContexts.at(-1);await current.suspend();current.resume=()=>Promise.reject(new DOMException('Suspended','NotAllowedError'));window.setVisibility('visible');});await pending.page.waitForFunction(()=>document.getElementById('toggle').dataset.state==='disabled');
    check('系統不允許恢復收音時停用並提供重開提示',await pending.page.locator('#error').innerText().then(text=>text.includes('重新開啟麥克風'))&&await pending.page.evaluate(()=>captureTracks.every(track=>track.readyState==='ended')&&wakeHandles.every(held=>held.released)));
    const unpaired=await open('success',false);await unpaired.page.locator('#error').waitFor({state:'visible'});check('未配對時不開啟收音或螢幕常亮',await unpaired.page.locator('#toggle').isDisabled()&&await unpaired.page.evaluate(()=>captureRequests.length===0&&wakeRequests===0));
    await start(denied);await denied.page.waitForFunction(()=>document.getElementById('screen-status').textContent.includes('無法保持'));
    for(const size of [{width:320,height:568},{width:844,height:390}]){await denied.page.setViewportSize(size);check(`${size.width}×${size.height} 長提示無橫向溢出`,await denied.page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1));}
    await denied.page.setViewportSize({width:390,height:844});await denied.page.evaluate(()=>document.documentElement.style.zoom='2');check('放大文字仍可捲動且沒有停用縮放',await denied.page.evaluate(()=>!document.querySelector('meta[name="viewport"]').content.includes('user-scalable=no')&&document.documentElement.scrollWidth<=innerWidth+1));await denied.page.screenshot({path:path.join(folder,'phone-mobile-large-text.png')});
    const setup=await first.context.newPage();active=setup;await setup.goto(info.SetupUrl);
    await setup.locator('#setup-actions').waitFor({state:'visible'});
    check('首次設定標題前的專案標誌正確載入',await setup.locator('header img').evaluate(image=>image.getAttribute('src')==='/logo.svg'&&image.complete&&image.naturalWidth>0));
    const panel=()=>setup.locator('[data-step]:visible');
    check('首次設定一次只顯示一個步驟，iPhone 共六步',await panel().count()===1&&await panel().getAttribute('data-step')==='download'&&await setup.locator('#setup-count').innerText()==='步驟 1 / 6'&&!await setup.locator('#setup-previous').isVisible());
    await setup.screenshot({path:path.join(folder,'phone-setup-download.png')});
    await setup.locator('summary').filter({hasText:'核對憑證指紋'}).click();
    check('下載前可展開核對這台測試電腦的公開憑證指紋',await setup.locator('code').isVisible()&&await setup.locator('code').innerText().then(text=>text.split(':').length===32));
    check('指紋與核對說明在下載按鈕上方',await setup.evaluate(()=>document.querySelector('code').getBoundingClientRect().bottom<=document.getElementById('download-certificate').getBoundingClientRect().top));
    await setup.screenshot({path:path.join(folder,'phone-setup-fingerprint.png')});
    await setup.locator('summary').filter({hasText:'核對憑證指紋'}).click();
    await setup.locator('#setup-next').click();
    check('第二步指引下載後由檔案開啟憑證',await panel().getAttribute('data-step')==='files'&&await panel().innerText().then(text=>text.includes('檔案 App → 下載項目')&&text.includes('OpenKaraoke-Local-CA.cer')));
    await setup.locator('#setup-next').click();
    await setup.locator('summary').filter({hasText:'找不到「已下載描述檔」'}).click();
    check('安裝步驟保留替代路徑與逾期重新開啟說明',await panel().innerText().then(text=>text.includes('VPN 與裝置管理')&&text.includes('重新點開憑證')));
    await setup.locator('#setup-next').click();
    check('第四步獨立呈現完整信任路徑與提醒',await panel().getAttribute('data-step')==='trust'&&await panel().innerText().then(text=>text.includes('憑證信任設定')&&text.includes('完整信任開關應為開啟'))&&await setup.locator('#setup-count').innerText()==='步驟 4 / 6');
    await setup.screenshot({path:path.join(folder,'phone-setup-trust.png')});
    await setup.reload();
    check('重新整理後保留目前步驟',await panel().getAttribute('data-step')==='trust'&&await setup.locator('#setup-count').innerText()==='步驟 4 / 6');
    await setup.locator('#setup-previous').click();
    check('可以回到上一個步驟',await panel().getAttribute('data-step')==='install');
    await setup.locator('#setup-next').click();await setup.locator('#setup-next').click();
    check('連接步驟明確指定第二個 QR Code，沒有跳過配對',await panel().getAttribute('data-step')==='connect'&&await panel().innerText().then(text=>text.includes('右側「連接麥克風」QR Code'))&&await setup.locator('a[href*="pair="]').count()===0);
    await setup.locator('#setup-next').click();
    await setup.locator('summary').filter({hasText:'螢幕常亮與停止收音'}).click();
    const setupText=await panel().innerText();
    check('最後步驟保留實際收音、套用、兩支上限與常亮說明',setupText.includes('收音中')&&setupText.includes('套用')&&setupText.includes('最多兩支')&&setupText.includes('螢幕常亮')&&!setupText.includes('手動收音增益')&&!setupText.includes('連線診斷'));
    await setup.locator('#setup-next').click();
    check('最後一步可從頭查看',await panel().getAttribute('data-step')==='download');
    await setup.getByRole('button',{name:'Android',exact:true}).click();
    check('Android 僅顯示四個適用步驟',await setup.locator('#setup-count').innerText()==='步驟 1 / 4');
    await setup.locator('#setup-next').click();
    check('Android 顯示 CA 安裝說明，不要求 iPhone 完整信任',await panel().getAttribute('data-step')==='android-install'&&await panel().innerText().then(text=>text.includes('CA 憑證')&&!text.includes('完整信任')));
    await setup.locator('#setup-next').click();
    check('Android 安裝後接續連接麥克風',await panel().getAttribute('data-step')==='connect'&&await setup.locator('#setup-count').innerText()==='步驟 3 / 4');
    await setup.locator('#setup-help summary').click();await setup.getByRole('link',{name:'確認完整信任'}).click();
    check('協助連結可切換到正確手機與步驟',await panel().getAttribute('data-step')==='trust'&&await setup.getByRole('button',{name:'iPhone',exact:true}).getAttribute('aria-pressed')==='true');
    const certificate=await first.context.request.get(info.SetupUrl+'ca.cer');const der=await certificate.body();
    check('首次設定仍可下載公開 CA 憑證',certificate.ok()&&new(require('node:crypto').X509Certificate)(der).ca);
    for(const size of [{width:320,height:568},{width:390,height:844},{width:844,height:390}]){
      await setup.setViewportSize(size);
      await setup.getByRole('button',{name:'iPhone',exact:true}).click();
      for(let step=0;step<6;step++){
        check(`${size.width}×${size.height} 第 ${step+1} 步只顯示一頁且無橫向溢出`,await panel().count()===1&&await setup.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1));
        await setup.locator('#setup-next').click();
      }
    }
    await setup.setViewportSize({width:390,height:844});
    await setup.screenshot({path:path.join(folder,'phone-setup-download.png')});
    check('首次下載頁不再需要捲動長篇說明',await setup.evaluate(()=>document.documentElement.scrollHeight<=innerHeight+1));
    await setup.locator('#setup-next').click();await setup.locator('#setup-next').click();await setup.locator('#setup-next').click();
    await setup.screenshot({path:path.join(folder,'phone-setup-trust.png')});
    await setup.setViewportSize({width:320,height:568});await setup.screenshot({path:path.join(folder,'phone-setup-narrow.png')});
    await setup.evaluate(()=>document.documentElement.style.zoom='2');
    check('首次設定放大文字仍可捲動且沒有橫向溢出',await setup.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1));
    await setup.evaluate(()=>document.documentElement.style.zoom='1');
    check('首次設定保持使用者縮放能力',await setup.evaluate(()=>!document.querySelector('meta[name="viewport"]').content.match(/user-scalable=no|maximum-scale=1/)));
    const noScript=await browser.newContext({javaScriptEnabled:false,viewport:{width:390,height:844}});const fallback=await noScript.newPage();await fallback.goto(info.SetupUrl);
    check('JavaScript 停用時仍可下載憑證及閱讀完整必要步驟',await fallback.locator('[data-step]').count()===7&&await fallback.locator('#download-certificate').isVisible()&&await fallback.locator('#iphone-trust').isVisible());await noScript.close();
    await stop(denied);const native=await open('native');await start(native);
    await native.page.waitForFunction(()=>!document.getElementById('screen-status').hidden&&!document.getElementById('screen-status').textContent.includes('正在'));
    const nativeResult=await native.page.evaluate(()=>({Supported:!!navigator.wakeLock,Acquired:!!window.nativeWakeHandle&&!window.nativeWakeHandle.released,Type:window.nativeWakeHandle?.constructor.name,Message:document.getElementById('screen-status').textContent}));
    check('實際 Edge 常亮 API 回報與畫面狀態一致',nativeResult.Acquired?nativeResult.Type==='WakeLockSentinel'&&nativeResult.Message.includes('螢幕常亮中'):nativeResult.Message.includes('無法保持')||nativeResult.Message.includes('不支援'),nativeResult);
    await stop(native);check('實際常亮 API 取得的控制權會在停用後釋放',await native.page.evaluate(()=>!window.nativeWakeHandle||window.nativeWakeHandle.released));
    const stale=await open();await stale.page.route('**/api/state',route=>route.fulfill({status:401,body:''}));await stale.page.locator('#toggle').click();await stale.page.locator('#error').waitFor({state:'visible'});
    check('配對失效提示重新掃描，不誤報額滿或取得麥克風',await stale.page.locator('#toggle').isDisabled()&&await stale.page.locator('#error').innerText().then(text=>text.includes('配對已失效'))&&await stale.page.evaluate(()=>captureRequests.length===0&&wakeRequests===0&&audioContexts.every(value=>value.state==='closed')));
    check('手機頁面沒有未處理的 JavaScript 錯誤',errors.length===0,errors);
  }catch(error){if(active)await active.screenshot({path:path.join(folder,'failure.png')});throw error;}finally{await browser.close();}
}
main().then(()=>fs.writeFileSync(path.join(folder,'report.json'),JSON.stringify({Passed:true,Checks:checks,Scope:'Edge 真實 HTTPS/WebSocket 與音訊；常亮 API 用可控替身驗證生命週期，非實體手機防鎖或端到端延遲量測。'},null,2))).catch(error=>{fs.writeFileSync(path.join(folder,'report.json'),JSON.stringify({Passed:false,Checks:checks,Error:error.stack},null,2));console.error(error);process.exitCode=1;}).finally(()=>fs.writeFileSync(infoPath+'.stop','stop'));
