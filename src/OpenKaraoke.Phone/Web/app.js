'use strict';
const byId=id=>document.getElementById(id),button=byId('toggle'),status=byId('status'),error=byId('error'),screenStatus=byId('screen-status');
let socket=null,context=null,stream=null,source=null,processor=null,generation=0,paired=false,slot=null,phase='disabled';
let pingId=0,pingTimer=null,clock=null,clockSamples=[];
let wakeLock=null,wakePending=false,wakeAttempt=0;
const pings=new Map(),pairing=new URLSearchParams(location.hash.slice(1)).get('pair');
function showError(text){error.textContent=text;error.hidden=!text;}
function showScreen(text=''){screenStatus.textContent=text;screenStatus.hidden=!text;}
function render(state,targetEnabled=false){
  phase=state;const changing=state==='changing',running=state==='enabled';
  button.textContent=changing?(targetEnabled?'連線中…':'停止收音…'):running?'關閉麥克風':'開啟麥克風';button.classList.toggle('primary',!running);button.dataset.state=state;
  button.setAttribute('aria-busy',String(changing));button.setAttribute('aria-pressed',String(running||(changing&&!targetEnabled)));button.disabled=!paired||changing;
}
function recordingStatus(){status.textContent=slot?'手機 '+slot+'・收音中':'收音中';}
async function releaseScreen(){
  wakeAttempt++;wakePending=false;const held=wakeLock;wakeLock=null;showScreen();
  try{await held?.release();}catch{}
}
async function keepScreen(){
  if(phase!=='enabled'||document.visibilityState!=='visible'||wakePending||wakeLock&&!wakeLock.released)return;
  if(!navigator.wakeLock?.request){showScreen('此瀏覽器不支援螢幕常亮，請暫時將手機自動鎖定設為「永不」，並保持此頁面開啟。');return;}
  const attempt=++wakeAttempt;wakePending=true;showScreen('正在要求保持螢幕常亮…');
  try{
    const held=await navigator.wakeLock.request('screen');
    if(attempt!==wakeAttempt||phase!=='enabled'||document.visibilityState!=='visible'){await held.release();return;}
    wakeLock=held;
    held.addEventListener('release',()=>{
      if(wakeLock!==held)return;wakeLock=null;
      if(phase==='enabled'&&document.visibilityState==='visible')showScreen('系統已解除螢幕常亮。請關閉低耗電模式，重新開啟麥克風，並避免手動鎖屏。');
    });
    showScreen(held.released?'系統未保持螢幕常亮，請保持此頁面開啟並避免鎖屏。':'螢幕常亮中；請保持此頁面開啟，避免手動鎖屏。');
  }catch{
    if(attempt===wakeAttempt&&phase==='enabled')showScreen('無法保持螢幕常亮。請關閉低耗電模式後重新開啟麥克風，或暫時將自動鎖定設為「永不」。');
  }finally{if(attempt===wakeAttempt)wakePending=false;}
}
function probe(ws){
  return new Promise((resolve,reject)=>{
    const id=++pingId,sent=performance.now(),timer=setTimeout(()=>finish(new Error('無法完成連線時間同步，請重新開啟麥克風。')),2000);
    function finish(error,value){clearTimeout(timer);pings.delete(id);error?reject(error):resolve(value);}
    pings.set(id,{sent,finish});try{ws.send(JSON.stringify({type:'ping',id}));}catch(error){finish(error);}
  });
}
function receiveControl(event){
  try{
    const value=JSON.parse(event.data);
    if(value.type==='hello'){slot=value.slot;return;}
    const pending=pings.get(value.id);if(value.type!=='pong'||!pending||!Number.isFinite(value.serverTime))return;
    const received=performance.now(),rtt=received-pending.sent;
    clockSamples.push({rtt,offset:value.serverTime-(received+pending.sent)/2});if(clockSamples.length>8)clockSamples.shift();
    clock=clockSamples.reduce((best,sample)=>sample.rtt<best.rtt?sample:best);pending.finish(null,value);
  }catch{}
}
function resetClock(){
  clearInterval(pingTimer);pingTimer=null;for(const pending of [...pings.values()])pending.finish(new Error('收音已停止。'));
  clock=null;clockSamples=[];
}
async function synchronizeClock(ws){
  resetClock();for(let i=0;i<3;i++)await probe(ws);
  if(socket!==ws)throw new Error('麥克風連線已中斷。');
  pingTimer=setInterval(()=>{if(socket===ws&&pings.size===0)void probe(ws).catch(()=>{});},2000);
}
async function stopRecording(message=''){
  render('changing',false);status.textContent='正在停止收音…';const stopped=++generation,old=socket;socket=null;resetClock();void releaseScreen();
  if(processor){processor.port.onmessage=null;processor.disconnect();processor=null;}
  source?.disconnect();source=null;stream?.getTracks().forEach(track=>track.stop());stream=null;
  const oldContext=context;context=null;old?.close();await oldContext?.close().catch(()=>{});
  if(generation!==stopped)return;
  render('disabled');status.textContent='尚未收音';showError(message);
}
async function resumeRecording(){
  if(phase!=='enabled'||document.visibilityState!=='visible')return;
  const current=context,ws=socket,attempt=generation;
  if(!current||!stream?.getAudioTracks().some(track=>track.readyState==='live')){await stopRecording('收音已中斷，請重新開啟麥克風。');return;}
  render('changing',true);status.textContent='正在恢復收音…';
  try{
    if(current.state!=='running')await current.resume();if(attempt!==generation)return;
    if(current.state!=='running'||ws?.readyState!==WebSocket.OPEN)throw new Error();
    await synchronizeClock(ws);if(attempt!==generation)return;
    if(current.state!=='running')await current.resume();
    if(attempt!==generation)return;if(current.state!=='running')throw new Error();
    render('enabled');recordingStatus();
  }catch{if(attempt===generation)await stopRecording('手機暫停了收音，請重新開啟麥克風。');return;}
  void keepScreen();
}
button.addEventListener('click',async()=>{
  if(phase==='changing'||!paired)return;if(phase==='enabled'){await stopRecording();return;}
  resetClock();render('changing',true);status.textContent='正在連線…';showError('');const attempt=++generation;
  try{
    if(!window.isSecureContext||!navigator.mediaDevices?.getUserMedia)throw new Error('請先完成手機憑證設定，再重新開啟配對網址。');
    const currentContext=new AudioContext({latencyHint:'interactive'});context=currentContext;await currentContext.resume();
    const response=await fetch('/api/state',{cache:'no-store'});if(attempt!==generation)return;
    if(response.status===401){paired=false;throw new Error('配對已失效，請重新掃描電腦上的「連接麥克風」。');}
    if(!response.ok)throw new Error('無法確認連線狀態，請確認電腦與區域網路。');
    const connection=await response.json();if(attempt!==generation)return;
    if(connection.connected)throw new Error('這支手機已在其他分頁收音，請先關閉另一個分頁的麥克風。');
    if(connection.count>=connection.limit)throw new Error('目前已有兩支手機收音，請先關閉其中一支。');
    const captured=await navigator.mediaDevices.getUserMedia({audio:{channelCount:1,echoCancellation:false,noiseSuppression:false,autoGainControl:false,latency:{ideal:.01}}});
    if(attempt!==generation){captured.getTracks().forEach(track=>track.stop());return;}stream=captured;
    const track=captured.getAudioTracks()[0],settings=track.getSettings?.()||{};
    if([settings.autoGainControl,settings.echoCancellation,settings.noiseSuppression].some(value=>value===true))showError('手機仍啟用內建音訊處理，可能增加延遲；可改用電腦有線麥克風。');
    const ws=new WebSocket('wss://'+location.host+'/ws');socket=ws;ws.onmessage=receiveControl;
    await new Promise((resolve,reject)=>{const timer=setTimeout(()=>reject(new Error('無法連接電腦，請確認區域網路。')),8000);ws.onopen=()=>{clearTimeout(timer);resolve();};ws.onerror=()=>{clearTimeout(timer);reject(new Error('無法連線，最多兩支手機同時收音。'));};ws.onclose=()=>{clearTimeout(timer);reject(new Error('麥克風連線已中斷。'));};});
    if(attempt!==generation)return;ws.onclose=()=>{if(socket===ws)void stopRecording('連線已中斷，請重新開啟麥克風。');};
    ws.send(JSON.stringify({sampleRate:currentContext.sampleRate,protocol:2}));
    await synchronizeClock(ws);
    await currentContext.audioWorklet.addModule('/audio-worklet.js');if(attempt!==generation)return;
    if(currentContext.state!=='running')await currentContext.resume();
    if(currentContext.state!=='running')throw new Error('手機無法開始收音，請保持此頁面開啟後重試。');
    source=currentContext.createMediaStreamSource(captured);processor=new AudioWorkletNode(currentContext,'openkaraoke-pcm',{numberOfInputs:1,numberOfOutputs:1,outputChannelCount:[1]});let sequence=0;
    processor.port.onmessage=event=>{
      const data=event.data;if(socket!==ws||ws.readyState!==WebSocket.OPEN||!(data?.pcm instanceof ArrayBuffer)||!clock)return;
      const queueMs=Math.max(0,(currentContext.currentTime-data.endFrame/currentContext.sampleRate)*1000);
      if(queueMs>30||ws.bufferedAmount+data.pcm.byteLength+32>currentContext.sampleRate*2*.03)return;
      const packet=new ArrayBuffer(32+data.pcm.byteLength),view=new DataView(packet);
      view.setUint32(0,0x324d4b4f,true);view.setUint32(4,++sequence,true);view.setFloat64(8,performance.now()+clock.offset-queueMs,true);view.setFloat64(16,data.endFrame,true);view.setFloat64(24,clock.rtt/2,true);
      new Uint8Array(packet,32).set(new Uint8Array(data.pcm));ws.send(packet);
    };
    source.connect(processor);processor.connect(currentContext.destination);
    render('enabled');recordingStatus();void keepScreen();
    currentContext.addEventListener('statechange',()=>{if(context===currentContext&&phase==='enabled'&&document.visibilityState==='visible'&&currentContext.state!=='running')void resumeRecording();});
    track.addEventListener('ended',()=>{if(stream===captured)void stopRecording('手機已停止收音，請重新開啟麥克風。');});
  }catch(e){if(attempt===generation){await stopRecording();showError(e.name==='NotAllowedError'?'請在瀏覽器允許麥克風後重試。':e.message||'無法開啟麥克風。');}}
});
document.addEventListener('visibilitychange',()=>{if(document.visibilityState==='visible')void resumeRecording();else void releaseScreen();});
window.addEventListener('pagehide',()=>void stopRecording());
async function pair(){
  try{
    if(pairing){const response=await fetch('/pair',{method:'POST',headers:{Authorization:'Bearer '+pairing}});if(!response.ok)throw new Error();history.replaceState(null,'',location.pathname);}
    const response=await fetch('/api/state',{cache:'no-store'});if(!response.ok)throw new Error();paired=true;render('disabled');status.textContent='尚未收音';
  }catch{render('disabled');status.textContent='尚未收音';showError('尚未配對，請重新掃描電腦上的「連接麥克風」。');}
}
render('disabled');void pair();
