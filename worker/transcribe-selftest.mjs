import transcribe from './transcribe.js';

function assert(condition, message) {
  if (!condition) throw new Error(message);
}

let lastModel;
let lastArgs;
const env = {
  HELPSYS_ASR_MODEL: '@cf/openai/whisper-large-v3-turbo',
  AI: {
    async run(model, args) {
      lastModel = model;
      lastArgs = args;
      return { text: '設定を開いてください' };
    }
  }
};

const wav = new Uint8Array([82, 73, 70, 70, 36, 0, 0, 0, 87, 65, 86, 69, 102, 109, 116, 32]);
const request = new Request('https://example.test/v1/transcribe', {
  method: 'POST',
  headers: { 'content-type': 'audio/wav' },
  body: wav
});
const response = await transcribe.fetch(request, env, {});
assert(response.status === 200, `unexpected transcription response ${response.status}`);
const value = await response.json();
assert(value.text === '設定を開いてください', 'transcription endpoint must return recognized text');
assert(lastModel === '@cf/openai/whisper-large-v3-turbo', 'transcription endpoint must use Whisper large-v3-turbo');
assert(typeof lastArgs?.audio === 'string' && lastArgs.audio.length > 0, 'transcription endpoint must send base64 audio');
assert(lastArgs.language === 'ja', 'transcription endpoint must pin Japanese');
assert(lastArgs.task === 'transcribe', 'transcription endpoint must use transcription mode');
assert(lastArgs.vad_filter === true, 'transcription endpoint must use VAD');
assert(lastArgs.condition_on_previous_text === false, 'short commands must not inherit prior transcription text');

const badType = await transcribe.fetch(new Request('https://example.test/v1/transcribe', {
  method: 'POST',
  headers: { 'content-type': 'text/plain' },
  body: 'not audio'
}), env, {});
assert(badType.status === 415, 'non-audio input must be rejected');

console.log('HelpSys Whisper transcription self-test passed.');
