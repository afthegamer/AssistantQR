# Image du service d'embeddings.
#
# Elle ne contient AUCUN poids de modele : le calcul des vecteurs est delegue a
# Ollama, qui tourne a cote (sur l'hote, ou dans le service du profil "ollama"
# de docker-compose.yml). L'image reste donc petite et se reconstruit vite --
# ce qui compte pour un depot pedagogique qu'on relance souvent.

FROM python:3.12-slim

ENV PYTHONUNBUFFERED=1 \
    PYTHONDONTWRITEBYTECODE=1 \
    PIP_NO_CACHE_DIR=1 \
    DATA_DIR=/data \
    SERVICE_HOST=0.0.0.0 \
    SERVICE_PORT=8088

WORKDIR /srv

# Les dependances d'abord, le code ensuite : une modification du code ne
# reinstalle pas numpy.
COPY requirements.txt ./
RUN pip install --no-cache-dir -r requirements.txt

COPY app ./app

# L'index est persiste hors de l'image (volume monte sur /data), sans quoi une
# reconstruction obligerait a reindexer tout le corpus.
RUN mkdir -p /data
VOLUME ["/data"]

EXPOSE 8088

# Pas de curl dans l'image slim : la sonde passe par urllib.
HEALTHCHECK --interval=15s --timeout=5s --start-period=20s --retries=5 \
    CMD python -c "import urllib.request,sys; sys.exit(0 if urllib.request.urlopen('http://127.0.0.1:8088/health', timeout=4).status == 200 else 1)"

CMD ["uvicorn", "app.main:app", "--host", "0.0.0.0", "--port", "8088"]
