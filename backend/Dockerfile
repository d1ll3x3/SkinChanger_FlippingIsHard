# Optional: container image for hosts that prefer Docker (Render, Koyeb, Fly, etc.).
# Render can also run this app natively via render.yaml without Docker.
FROM python:3.12-slim

WORKDIR /app
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt
COPY . .

ENV PORT=8000
EXPOSE 8000
CMD ["sh", "-c", "uvicorn app:app --host 0.0.0.0 --port ${PORT:-8000}"]
