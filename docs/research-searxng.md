# Pesquisa web com SearXNG

O Nebula usa o SearXNG como uma opcao gratuita e self-hosted de pesquisa web.
Ele roda junto da stack Docker Compose e expoe a API JSON que o modulo de
aprendizado consulta antes do fallback Bing HTML.

## Subir o servico

```bash
docker compose up -d searxng
```

Por padrao, a interface fica disponivel em:

```text
http://localhost:8080
```

A porta do host pode ser alterada com:

```env
SEARXNG_PORT=8080
```

O Compose gera `SEARXNG_SECRET` em runtime quando a variavel nao e definida.
Defina `SEARXNG_SECRET` em `.env` se precisar de sessoes estaveis entre
recriacao de containers.

## Testar a API JSON

```text
http://localhost:8080/search?q=dotnet&format=json
```

Se esse endpoint retornar `403 Forbidden`, confira se
`docker/searxng/settings.yml` contem:

```yaml
search:
  formats:
    - html
    - json
```

## Configurar o Nebula

Nebula rodando fora do Docker:

```env
Research__SearXng__BaseUrl=http://localhost:8080
```

Nebula rodando dentro do Docker Compose:

```env
Research__SearXng__BaseUrl=http://searxng:8080
```

Configuracao completa:

```env
Research__SearXng__Enabled=true
Research__SearXng__BaseUrl=http://searxng:8080
Research__SearXng__MaxResults=10
Research__SearXng__TimeoutSeconds=20
```

Para usar apenas SearXNG como provider de busca:

```env
WebResearch__Provider=SearXng
```

Com `WebResearch__Provider=Free`, o Nebula tenta documentacao direta primeiro,
depois SearXNG e, por ultimo, Bing HTML.

## Endpoint de teste do Nebula

Com o app web em execucao:

```http
GET /api/research/search?q=boas%20praticas%20powershell
```

Resposta esperada:

```json
{
  "query": "boas praticas powershell",
  "providerResults": [
    {
      "provider": "SearXng",
      "title": "...",
      "url": "...",
      "snippet": "...",
      "score": 0.9
    }
  ]
}
```

## Diagnostico

Container nao sobe:

- Rode `docker compose logs -f searxng`.
- Confira se `docker/searxng/settings.yml` e valido.
- Se a porta 8080 ja estiver em uso, altere `SEARXNG_PORT`.

Endpoint JSON nao responde:

- Teste `http://localhost:8080` no navegador.
- Teste `http://localhost:8080/search?q=dotnet&format=json`.
- Confirme se `json` esta habilitado em `search.formats`.

Nenhum resultado retornado:

- Alguns engines externos podem bloquear ou limitar a instancia.
- Tente outra consulta simples, como `dotnet`.
- Verifique logs do SearXNG e logs `[AGENT] Web research` do Nebula.

Timeout:

- Ajuste `Research__SearXng__TimeoutSeconds`.
- Confira conectividade entre containers usando a URL interna
  `http://searxng:8080`.

SearXNG indisponivel:

- O provider retorna lista vazia e registra o erro.
- O fluxo principal do Nebula continua sem derrubar a aplicacao.
