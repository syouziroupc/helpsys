const DEFAULT_MODEL = '@cf/openai/whisper-large-v3-turbo';
const MAX_AUDIO_BYTES = 1_000_000;

export default {
  async fetch(request, env) {
    let url;
    try { url = new URL(request.url); }
    catch { return json({ error: 'bad_url' }, 400); }

    if (request.method !== 'POST' || url.pathname !== '/v1/transcribe') return json({ error: 'not_found' }, 404);
    if (!authorized(request, env)) return json({ error: 'unauthorized' }, 401);

    const contentType = (request.headers.get('content-type') || '').toLowerCase();
    if (!contentType.startsWith('audio/wav') && !contentType.startsWith('audio/x-wav'))
      return json({ error: 'unsupported_audio' }, 415);

    const declaredLength = Number(request.headers.get('content-length') || 0);
    if (Number.isFinite(declaredLength) && declaredLength > MAX_AUDIO_BYTES)
      return json({ error: 'audio_too_large' }, 413);

    let audio;
    try { audio = await request.arrayBuffer(); }
    catch { return json({ error: 'invalid_audio' }, 400); }
    if (!audio.byteLength || audio.byteLength > MAX_AUDIO_BYTES)
      return json({ error: audio.byteLength ? 'audio_too_large' : 'invalid_audio' }, audio.byteLength ? 413 : 400);

    const model = selectModel(env.HELPSYS_ASR_MODEL);
    try {
      const result = await env.AI.run(model, {
        audio: arrayBufferToBase64(audio),
        task: 'transcribe',
        language: 'ja',
        vad_filter: true,
        beam_size: 5,
        condition_on_previous_text: false,
        no_speech_threshold: 0.55,
        initial_prompt: '日本語の短いパソコン操作依頼。Windows、Chrome、Edge、Excel、Word、PowerPoint、設定、ファイル、フォルダーなどの語を正確に文字起こしする。'
      });

      const text = typeof result?.text === 'string' ? result.text.trim() : '';
      return json({ text, model });
    } catch (error) {
      console.error('transcription failed', error);
      return json({ error: 'transcription_failed' }, 502);
    }
  }
};

function arrayBufferToBase64(buffer) {
  const bytes = new Uint8Array(buffer);
  const chunkSize = 0x8000;
  let binary = '';
  for (let offset = 0; offset < bytes.length; offset += chunkSize) {
    binary += String.fromCharCode(...bytes.subarray(offset, Math.min(offset + chunkSize, bytes.length)));
  }
  return btoa(binary);
}

function selectModel(value) {
  return value === DEFAULT_MODEL ? value : DEFAULT_MODEL;
}

function authorized(request, env) {
  if (!env.HELPSYS_API_KEY) return true;
  return (request.headers.get('x-helpsys-key') || '') === env.HELPSYS_API_KEY;
}

function json(value, status = 200) {
  return new Response(JSON.stringify(value), {
    status,
    headers: { 'content-type': 'application/json; charset=utf-8' }
  });
}
