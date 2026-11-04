'use strict';
class MicrophonePcm extends AudioWorkletProcessor {
  constructor(){super();this.packet=new Int16Array(Math.round(sampleRate/100));this.used=0;}
  process(inputs,outputs){
    for(const channel of outputs[0]||[])channel.fill(0);
    const input=inputs[0]?.[0];if(!input)return true;
    for(let index=0;index<input.length;index++){
      const value=Math.max(-1,Math.min(1,input[index]));
      this.packet[this.used++]=value<0?Math.round(value*32768):Math.round(value*32767);
      if(this.used===this.packet.length){this.port.postMessage({pcm:this.packet.buffer,endFrame:currentFrame+index+1},[this.packet.buffer]);this.packet=new Int16Array(Math.round(sampleRate/100));this.used=0;}
    }
    return true;
  }
}
registerProcessor('openkaraoke-pcm',MicrophonePcm);
