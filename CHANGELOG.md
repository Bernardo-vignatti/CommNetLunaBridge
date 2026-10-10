# Histórico de versões

## 0.1.1 experimental — 2026-10-10

- Corrige o handshake no KSP usando o JsonFx que acompanha o Luna, sem depender de System.Runtime.Serialization no cliente.
- Mantém o formato do estado no servidor; atualize ambas as DLLs sem remigrar o universo.
- Acrescenta 14 verificações de JSON e 12 no Mono do KSP, incluindo compatibilidade entre os serializadores.

## 0.1.0 experimental — 2026-10-10

Primeira pré-release de um complemento independente para sincronizar CommNet Constellation via Luna Multiplayer.

- Cliente KSP e plugin Luna com estado autorizado pelo servidor, revisões, IDs estáveis, deduplicação, persistência atômica e ressincronização.
- Redes, estações existentes, antenas e listas manuais compartilhadas; preferências visuais permanecem locais.
- Migração offline explícita com backup e hashes; transporte confiável sobre o próprio Luna.
- Fonte, documentação de instalação/remoção, roteiro de aceitação e licenças incluídos.

Alvos: KSP 1.12.5.3190, Constellation 1.5.7, Luna 0.29.2 e Harmony 2.2.1. Compilação e testes locais passaram. A exportação pública recompilada e o núcleo sintético são verificados antes da publicação. Dois clientes KSP, física, UI, reconexão e docking continuam pendentes. Upgrades de estação em carreira e criação/exclusão de estações não são suportados nesta versão. Use cópias de teste e backups; esta versão não é estável.
