"""OpenKaraoke local Demucs adapter. Only the pinned, verified official model is loaded."""
import argparse
import hashlib
import json
from pathlib import Path
import os
import sys

MODEL_SHA256 = "8726e21a993978c7ba086d3872e7608d7d5bfca646ca4aca459ffda844faa8b4"


def emit(**data):
    print(json.dumps(data, ensure_ascii=False), flush=True)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--check", action="store_true")
    parser.add_argument("--model")
    parser.add_argument("--input")
    parser.add_argument("--output")
    parser.add_argument("--vocals")
    args = parser.parse_args()
    import torch
    import soundfile as sf
    import numpy as np
    from demucs.apply import apply_model
    from demucs.states import load_model

    if args.check:
        emit(ready=True, torch=torch.__version__, device="cuda" if torch.cuda.is_available() else "cpu")
        return
    model_path = Path(args.model)
    if hashlib.sha256(model_path.read_bytes()).hexdigest() != MODEL_SHA256:
        raise ValueError("AI 模型檔案驗證失敗。")
    package = torch.load(str(model_path), map_location="cpu", weights_only=False)
    model = load_model(package)
    model.eval()
    audio, sample_rate = sf.read(args.input, dtype="float32", always_2d=True)
    if sample_rate != model.samplerate or audio.shape[1] != model.audio_channels:
        raise ValueError("AI 輸入音訊規格不符。")
    torch.set_num_threads(max(1, min(8, (os.cpu_count() or 2) // 2)))
    waveform = torch.from_numpy(audio.T.copy())
    ref = waveform.mean(0)
    mean, std = ref.mean(), ref.std().clamp(min=1e-8)
    waveform = (waveform - mean) / std
    device = "cuda" if torch.cuda.is_available() else "cpu"
    emit(stage="separation", device=device)

    def separate(target):
        with torch.inference_mode():
            return apply_model(model, waveform[None], device=target, shifts=1, split=True,
                               overlap=0.25, segment=7, progress=True, num_workers=0)[0]

    try:
        sources = separate(device)
    except torch.cuda.OutOfMemoryError:
        torch.cuda.empty_cache()
        emit(stage="separation", device="cpu", reason="GPU 記憶體不足，改以 CPU 繼續。")
        sources = separate("cpu")
    sources = sources.cpu() * std + mean
    indices = [i for i, name in enumerate(model.sources) if name != "vocals"]
    accompaniment = sources[indices].sum(0).T.numpy()
    if not np.isfinite(accompaniment).all():
        raise ValueError("AI 輸出含有無效音訊。")
    peak = max(1.0, float(np.abs(accompaniment).max()) / 0.99)
    sf.write(args.output, accompaniment / peak, sample_rate, subtype="PCM_24")
    if args.vocals:
        vocals = sources[model.sources.index("vocals")].T.numpy()
        if not np.isfinite(vocals).all():
            raise ValueError("AI 人聲輸出含有無效音訊。")
        sf.write(args.vocals, vocals, sample_rate, subtype="FLOAT")
    emit(stage="complete", samples=len(accompaniment), sample_rate=sample_rate, device=device)


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        emit(error=str(error))
        raise
