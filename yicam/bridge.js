'use strict';

// Offsets are for the SHA-256 checked 1.0.1.1_202209261648 Windows client only.
const base = Process.mainModule.base;
const codec = Process.getModuleByName('avcodec-57.dll');
const cameras = new Map();
let frameCount = 0;
let capture = true;
let lastDimensions = '';

function currentCamera() {
  const active = Array.from(cameras.values()).filter(c => Date.now() - c.seen < 5000);
  if (active.length !== 1) throw Error('Open exactly one live camera in YI IOT.');
  const camera = active[0].pointer;
  if (camera.add(0x14c).readS32() < 0) throw Error('Camera connection has closed.');
  return camera;
}

// Observe instructions AFTER the blocking PPPP_Read calls. Hooking the read's
// onLeave keeps an outstanding invocation alive and can prevent clean detach.
for (const offset of [0x3ce989, 0x3cea6d]) {
  Interceptor.attach(base.add(offset), function () {
    try {
      const camera = this.context.edi;
      cameras.set(camera.toString(), {pointer:camera, seen:Date.now()});
      const stack = this.context.ebp;
      const channel = stack.sub(0x50).readU8();
      if (channel !== 0 || this.context.eax.toInt32() !== 0) return;
      const size = stack.sub(0x4c).readS32();
      if (size < 40 || size > 4096) return;
      const bytes = new Uint8Array(stack.sub(0x44).readPointer().readByteArray(size));
      const command = (bytes[0] << 8) | bytes[1];
      // Do not expose the 32-byte authentication field or arbitrary replies.
      if (command === 0x1301) {
        const text = Array.from(bytes.slice(40), c => c >= 32 && c <= 126 ? String.fromCharCode(c) : ' ').join('');
        const version = text.match(/\d+\.\d+\.\d+\.\d+[A-Za-z]?(?:_\d+)?/);
        if (version) send({kind:'firmware', version:version[0]});
      } else if (command === 0x1312) {
        send({kind:'resolution_reply', body:Array.from(bytes.slice(40, 56))});
      }
    } catch (e) { send({kind:'warning', message:String(e)}); }
  });
}

Interceptor.attach(codec.getExportByName('avcodec_decode_video2'), {
  onEnter(args) {
    this.frame = args[1];
    this.got = args[2];
    this.data = null;
    try {
      const size = args[3].add(28).readS32();
      if (capture && size > 0 && size <= 8 * 1024 * 1024) {
        this.data = args[3].add(24).readPointer().readByteArray(size);
        this.timestamp = Date.now();
        this.decoder = args[0].toString();
      }
    } catch (e) { send({kind:'warning', message:String(e)}); }
  },
  onLeave() {
    try {
      if (this.data === null) return;
      let width = 0, height = 0;
      if (this.got.readS32()) {
        width = this.frame.add(68).readS32();
        height = this.frame.add(72).readS32();
      }
      send({kind:'video', timestamp:this.timestamp, decoder:this.decoder,
        index:frameCount++, width, height}, this.data);
    } catch (e) { send({kind:'warning', message:String(e)}); }
  }
});

const nativeSend = new NativeFunction(base.add(0x3cee90), 'int',
  ['pointer','uint','pointer','int','pointer','int'], 'mscdecl');

rpc.exports = {
  pairing() {
    const camera = currentCamera();
    const value = camera.add(0xb0);
    const length = value.add(16).readU32();
    const capacity = value.add(20).readU32();
    if (length !== 15 || capacity > 4096) throw Error('Unexpected device-key layout.');
    const data = capacity < 16 ? value : value.readPointer();
    return {password:data.readUtf8String(length)};
  },
  state() {
    const camera = currentCamera();
    return {resolution:camera.add(0xd4).readS32(), useCount:camera.add(0xc8).readU8(), frames:frameCount};
  },
  firmware() {
    return nativeSend(currentCamera(), 0x1300, ptr(0), 0, ptr(0), 0);
  },
  resolution(mode) {
    if (![0,1,2,3].includes(mode)) throw Error('Unsupported resolution mode.');
    const camera = currentCamera();
    const payload = Memory.alloc(8);
    payload.writeByteArray([0,0,0,mode,0,0,0,camera.add(0xc8).readU8()]);
    const result = nativeSend(camera, 0x1311, payload, 8, ptr(0), 0);
    if (result < 0) throw Error('Camera command failed: ' + result);
    return {bytesSent:result, requestedMode:mode};
  },
  stop() { capture = false; Interceptor.detachAll(); }
};
send({kind:'ready', pid:Process.id});
