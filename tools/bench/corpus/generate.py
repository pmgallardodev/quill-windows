#!/usr/bin/env python3
"""Generates the synthetic Spanish meeting corpus used by Quill.Bench.

Every meeting is an authored list of turns. Turns that carry ground truth
(decisions, action items, open questions, risks) are written by hand and tagged
with answer-key entries. Between them the generator inserts neutral filler built
combinatorially from opener + body + closer fragments so that the transcript
reaches the target duration without introducing new facts. Filler bodies are
written to be non-committal: they never decide, assign, ask or warn.

Output (deterministic, seeded):
  <id>/transcript.json  Quill transcript shape: engine, model, created_at, segments[speaker,start_ms,end_ms,text]
  <id>/key.json         Answer key with timestamps of the turns that state each item

Run:  python3 -I generate.py   (writes next to this file)
"""
import json
import random
import unicodedata
from dataclasses import dataclass, field
from pathlib import Path

WORDS_PER_SECOND = 2.6
TURN_OVERHEAD_S = 1.35  # mean extra duration jitter (0.55 s) + mean gap (0.8 s)
CREATED_AT = "2026-11-23T09:00:00+00:00"


@dataclass
class K:
    cat: str  # decisions | actionItems | openQuestions | risks | superseded | acceptable
    id: str
    text: str
    keywords: list
    owner: str | None = None  # action items only; None means "unassigned / ambiguous"


@dataclass
class T:
    speaker: str
    text: str
    keys: list = field(default_factory=list)
    overlap: bool = False  # starts before the previous turn ends (crosstalk)


OPENERS = ["", "", "", "Sí, ", "Vale, ", "Ya, ", "A ver, ", "Mira, ", "Bueno, ", "Claro, ", "Eh, ",
           "O sea, ", "Pues ", "Hmm, ", "Exacto, ", "Total, ", "Y nada, ", "Fíjate que "]
CLOSERS = ["", "", "", "", " ¿no?", " la verdad.", " vamos.", " digo yo.", " o eso creo.",
           " ¿sabes?", " en general.", " por lo que he visto."]
GENERIC = [
    "yo lo veo bastante parecido a lo que comentabas antes",
    "esto ya nos pasó en el proyecto anterior y al final salió bien",
    "me parece razonable el enfoque tal y como está planteado",
    "es un tema que da para mucho pero no quiero alargarme",
    "lo importante es que todos tengamos el mismo contexto",
    "creo que en lo general estamos alineados",
    "a mí me cuadra lo que estáis contando",
    "es verdad que el último mes ha sido bastante intenso",
    "eso lo hablamos en su momento y la idea sigue siendo la misma",
    "me acuerdo de cuando lo montamos la primera vez y era mucho más lento",
    "ahí hay matices pero en el fondo coincido",
    "la gente con la que he hablado tiene una impresión parecida",
    "lo veo como una evolución natural de lo que ya teníamos",
    "no es algo nuevo pero sí conviene tenerlo presente",
    "en otros equipos lo han resuelto de forma similar",
    "el contexto ha cambiado un poco desde la última vez",
    "es más una cuestión de costumbre que de otra cosa",
    "cuando lo explicas así se entiende mucho mejor",
    "eso encaja con lo que vimos en los números del trimestre",
    "tampoco es que haya una única forma correcta de hacerlo",
    "lo he estado pensando durante el fin de semana y sigo opinando igual",
    "me gusta que lo estemos hablando con calma",
    "hay que reconocer que el equipo ha respondido muy bien",
    "a veces nos complicamos más de la cuenta con estas cosas",
    "es curioso porque al principio pensaba lo contrario",
    "el feedback que nos llegó fue bastante positivo en general",
    "eso me recuerda a una conversación que tuvimos hace meses",
    "ya sabemos cómo suelen ir estas cosas",
    "yo no le daría más vueltas de las necesarias",
    "en resumen es lo que estábamos comentando",
    "perdona que vuelva un momento sobre lo de antes",
    "te sigo, te sigo",
    "sí, sí, eso está claro",
    "vale, entendido",
    "perfecto, me sirve",
    "ajá",
    "correcto",
    "eso es",
    "ya veo",
    "tiene sentido",
]
DIGRESSIONS = [
    "por cierto, ¿visteis el partido de ayer? qué final más tremendo",
    "uf, la máquina de café de la tercera planta sigue estropeada",
    "perdón, que me están llamando al móvil, ya está, ya está",
    "¿se me oye bien? es que tengo la conexión un poco regular hoy",
    "esto me recuerda a las vacaciones, que ya queda poco para el puente",
    "por cierto, ¿sabéis que Pepe ha sido padre? enhorabuena desde aquí",
    "qué calor hace hoy en la oficina, no hay quien trabaje",
    "un segundo que cierro la ventana que hay mucho ruido en la calle",
    "me he quedado sin batería en los cascos, cambio al portátil",
    "no os he dicho que al final me compré la bici, estoy encantado",
]
MESSY = ["eh", "o sea", "bueno", "vale vale", "no no", "espera", "perdón", "sí sí", "a ver a ver"]


def words(text: str) -> int:
    return max(1, len(text.split()))


def filler(rng: random.Random, topical: list) -> str:
    pool = topical if rng.random() < 0.55 else GENERIC
    body = rng.choice(pool)
    text = rng.choice(OPENERS) + body + rng.choice(CLOSERS)
    text = text[0].upper() + text[1:]
    if not text.endswith((".", "?", "!")):
        text += "."
    return text


def asr_mess(rng: random.Random, text: str) -> str:
    """Mimic raw ASR output: hesitations, dropped punctuation, lower case."""
    if rng.random() < 0.35:
        text = rng.choice(MESSY) + " " + text[0].lower() + text[1:]
    if rng.random() < 0.3:
        text = text.replace(",", "").replace("¿", "").replace("¡", "")
    if rng.random() < 0.15:
        text = text.rstrip(".")
    return text


def build(meeting: dict) -> tuple[dict, dict]:
    rng = random.Random(meeting["seed"])
    script: list[T] = meeting["script"]
    speakers = list(meeting["speakers"].keys())
    target_s = meeting["minutes"] * 60
    key_words = sum(words(t.text) for t in script)
    key_s = key_words / WORDS_PER_SECOND + len(script) * TURN_OVERHEAD_S
    budget_s = max(0.0, target_s - key_s)
    gaps = len(script)  # filler goes before each scripted turn (and after the last)
    per_gap_s = budget_s / (gaps + 1)

    segments, key_items = [], []
    clock_ms = 2000
    last_end = clock_ms

    def emit(speaker: str, text: str, overlap: bool = False) -> int:
        nonlocal clock_ms, last_end
        dur = int((words(text) / WORDS_PER_SECOND) * 1000) + rng.randint(200, 900)
        if overlap and segments:
            start = max(segments[-1]["start_ms"] + 300, last_end - rng.randint(600, 1800))
        else:
            start = clock_ms
        end = start + dur
        segments.append({"speaker": speaker, "start_ms": start, "end_ms": end, "text": text})
        last_end = max(last_end, end)
        clock_ms = last_end + rng.randint(250, 1400)
        return start

    def fill(seconds: float, prev_speaker: str | None):
        spent = 0.0
        speaker = prev_speaker
        while spent < seconds:
            if meeting.get("messy") and rng.random() < 0.05:
                text = rng.choice(DIGRESSIONS)
                text = text[0].upper() + text[1:] + "."
            else:
                text = filler(rng, meeting["topical"])
            choices = [s for s in speakers if s != speaker] or speakers
            speaker = rng.choice(choices)
            overlap = bool(meeting.get("messy")) and rng.random() < 0.12
            if meeting.get("messy"):
                text = asr_mess(rng, text)
            emit(speaker, text, overlap)
            spent += words(text) / WORDS_PER_SECOND + TURN_OVERHEAD_S
        return speaker

    prev = None
    for turn in script:
        prev = fill(per_gap_s * rng.uniform(0.5, 1.5), prev)
        text = asr_mess(rng, turn.text) if meeting.get("messy") and not turn.keys else turn.text
        start = emit(turn.speaker, text, turn.overlap)
        prev = turn.speaker
        for k in turn.keys:
            item = {"id": k.id, "text": k.text, "keywords": k.keywords, "at_ms": start, "speaker": turn.speaker}
            if k.cat == "actionItems":
                item["owner"] = k.owner
                item["isMine"] = k.owner == "me"
            key_items.append((k.cat, item))
    fill(per_gap_s, prev)

    transcript = {"engine": "synthetic", "model": "quill-bench-corpus-v1", "created_at": CREATED_AT,
                  "segments": segments}
    key = {"meeting": meeting["id"], "title": meeting["title"], "speakers": meeting["speakers"],
           "duration_ms": segments[-1]["end_ms"], "notes": meeting.get("notes", "")}
    for cat in ["decisions", "actionItems", "openQuestions", "risks", "superseded", "acceptable"]:
        key[cat] = [i for c, i in key_items if c == cat]
    return transcript, key


def strip_accents(s: str) -> str:
    return "".join(c for c in unicodedata.normalize("NFD", s) if unicodedata.category(c) != "Mn")


def check_keywords(meeting: dict, transcript: dict, key: dict):
    """Every keyword must appear in the transcript, otherwise the key is unmatchable."""
    corpus = strip_accents(" ".join(s["text"] for s in transcript["segments"]).lower())
    for cat in ["decisions", "actionItems", "openQuestions", "risks", "superseded", "acceptable"]:
        for item in key[cat]:
            for kw in item["keywords"]:
                if strip_accents(kw.lower()) not in corpus:
                    raise SystemExit(f"{meeting['id']} {item['id']}: keyword '{kw}' not in transcript")


def main():
    from meetings import MEETINGS  # noqa: E402  (local module, same directory)

    out = Path(__file__).resolve().parent
    for meeting in MEETINGS:
        transcript, key = build(meeting)
        check_keywords(meeting, transcript, key)
        d = out / meeting["id"]
        d.mkdir(exist_ok=True)
        (d / "transcript.json").write_text(json.dumps(transcript, ensure_ascii=False, indent=1) + "\n", encoding="utf-8")
        (d / "key.json").write_text(json.dumps(key, ensure_ascii=False, indent=1) + "\n", encoding="utf-8")
        mins = key["duration_ms"] / 60000
        n = sum(len(key[c]) for c in ["decisions", "actionItems", "openQuestions", "risks"])
        print(f"{meeting['id']}: {len(transcript['segments'])} segments, {mins:.1f} min, {n} key items")


if __name__ == "__main__":
    import sys
    sys.path.insert(0, str(Path(__file__).resolve().parent))
    main()
