# CommNet Luna Bridge 0.1.2 — experimental

Complemento para sincronizar CommNet Constellation durante sessões Luna Multiplayer. Implementação preparada para **KSP 1.12.5.3190, Constellation 1.5.7, Luna cliente/servidor 0.29.2 e Harmony 2.2.1**. O assembly do Constellation se identifica como 1.5.0.0; a versão de arquivo correta é 1.5.7.

**As DLLs compilam e os testes automatizados passaram, incluindo serialização no Mono do KSP. Ainda não houve validação dentro de dois clientes KSP.** Preserve backups do servidor/universo e dos mods antes de testar. Não é necessário duplicar toda a instalação do KSP.

### Atualizar da 0.1.0 ou 0.1.1

Com o KSP e o servidor encerrados, substitua as duas DLLs do bridge pelos arquivos 0.1.2 do pacote. Cliente e servidor precisam da mesma versão. Preserve `Universe/CommNetLunaBridge`; a atualização não exige nova migração. A 0.1.1 corrigiu uma dependência ausente no KSP que impedia o handshake e causava desconexão após 30 segundos, inclusive ao entrar na Tracking Station. A 0.1.2 remove JSON/hash das comparações periódicas, evita capturar repetidamente naves ainda sem estado oficial e só recalcula antenas quando há mudança aplicável.

## Comportamento implementado

- Criar rede, editar nome/cor/frequência e excluir rede por operações com revisão.
- Preservar a identidade da rede quando sua frequência muda. Exclusão remapeia referências para a frequência pública; colisões são rejeitadas. Lista manual que ganha frequências duplicadas conserva a maior potência para aquela frequência.
- Alterar estações existentes: nome, cor, frequências, nível, corpo e coordenadas. Aplicar diretamente às instâncias e regenerar seus arrays de frequência. Nível do KSC continua sob o sistema de instalações do KSP.
- Alterar frequência, nome e uso de antenas com locks do Luna. Aplicar em peças carregadas e snapshots de peças não carregadas. Modos `AutoBuild` e `LockList`; potência dinâmica de `AutoBuild` continua calculada pelo Constellation.
- Servidor valida, persiste e publica o estado aceito. Cliente aplica em `Update`, na thread principal, e atualiza o painel de controle aberto. Recebimento não chama `OnLoad`/`Start` repetidamente.
- Snapshot completo ao entrar, ressincronização e repetição da operação após cinco segundos sem confirmação. Revisões antigas não substituem novas; IDs deduplicados ficam persistidos por até 1.024 operações.
- Canal confiável do Luna, sem servidor TCP/HTTP adicional. LunaCompat foi estudado como referência; o bridge não depende dele.

## Instalação para teste

1. Encerre os clientes e o servidor de teste. Copie e preserve seu `Universe` completo, as configurações do servidor e os mods dos clientes.
2. Em **cada cliente**, copie `Client/GameData/CommNetLunaBridge` para `GameData`. Mantenha os mods originais e o Harmony já instalado. Não copie assemblies do KSP de outra instalação.
3. No **servidor**, copie `Server/Plugins/CommNetLunaBridge.Server.dll` para `Plugins`. O servidor continua sendo o Luna 0.29.2 existente.
4. Prepare a migração offline a partir do universo que será usado no teste. No PowerShell, dentro do pacote extraído:

   ```powershell
   & .\MigrationTool\CommNetLunaBridge.Migrate.exe 'D:\LMP-teste\Universe' 'D:\Migracao-CNC' 'JogadorEditor' 0
   ```

   O último argumento é a frequência pública, conferida em `cnc_settings.cfg`. No exemplo ela é `0`; confira a sua instalação. A ferramenta lê `Scenarios/CNCCommNetScenario.txt` e os arquivos de naves, cria `state.json`, `settings.json` e um backup com hashes, e preserva a origem. Recusa substituir um `state.json` existente. Falha de parsing/validação interrompe a migração. Frequências órfãs existentes recebem redes chamadas `Importada <frequência>` para preservar referências.

5. Revise os arquivos gerados. Copie a pasta de saída completa para `Universe/CommNetLunaBridge` no servidor de teste. Não remigre sobre um estado já em uso. Em servidores novos, prepare primeiro um cenário Constellation deliberadamente escolhido; não há importação automática de um cliente.
6. Ajuste `settings.json`, acrescentando os nomes exatos dos jogadores autorizados em `Editors`. Os demais jogadores podem editar antenas quando possuem os locks apropriados, mas não o catálogo/estações. Acrescente corpos personalizados em `Bodies` se usar planetas adicionais.
7. Se o controle de mods do Luna usar whitelist, autorize o caminho/hash da nova DLL do cliente na configuração da **cópia de teste**. Consulte o mecanismo já usado pelo seu servidor; o pacote não altera automaticamente essa política.
8. Inicie o servidor e os dois clientes, seguindo [o roteiro de aceitação](docs/TESTE-MULTIPLAYER.md). Só promova a instalação depois de registrar os resultados.

Naves antigas sem módulos CNC entram pelo fluxo normal de snapshots quando o Constellation adiciona esses módulos. A distribuição não inclui universos, saves ou configurações de jogadores.

## Persistência e política de clientes

`Universe/CommNetLunaBridge/state.json` é a autoridade; inclui esquema, época, revisão e recibos. Escrita temporária com flush e substituição atômica; `state.json.bak` guarda a revisão anterior. Se o arquivo principal estiver inválido, o plugin restaura o backup válido e guarda cópias `.corrupt-*` e `.recovery`. Se ambos forem inválidos, bloqueia o serviço e informa o problema. Uma recuperação de backup pode perder a última revisão; confira o log antes de permitir novas edições.

O cenário CNC do Luna é apenas uma projeção para o carregamento do KSP. O plugin descarta os cenários CNC/CommNet enviados pelos clientes, preservando os demais módulos do mesmo lote. Protos normais de nave recebem uma sobreposição dos campos CNC aceitos antes de seguirem para o armazenamento e retransmissão usuais. O bridge não modifica órbitas, recursos ou campos de outros mods.

Clientes sem o bridge ou com versão diferente são desconectados: o handshake é obrigatório após autenticação, com prazo de 30 segundos. O cliente com bridge e servidor sem plugin permanece sem autorização para editar e mostra timeout. Uma falha de aplicação encerra a conexão para evitar continuar com estado parcialmente aplicado.

## Campos compartilhados e pessoais

| Dados | Política |
|---|---|
| Frequência, nome e cor da rede | Compartilhados; ID estável no servidor |
| Nome/cor/nível/frequências/geometria de estação | Compartilhados; ID nativo estável |
| `Frequency`, `OptionalName`, `InUse` de antena | Compartilhados; chave nave + `flightID`/`uid` + índice do módulo |
| `FreqListOperation`, lista/potências de `LockList` | Compartilhados |
| Potências/array agregados de `AutoBuild` | Recalculados pelo Constellation; não congelados pelo servidor |
| Visibilidade de rede, `HideGroundStations`, modos de mapa, cor de órbita legada | Pessoais; usados na UI/desenho, excluídos do protocolo |
| Seleção de nave, filtros e ordenação | Pessoais |
| `CosAngle`, `AntennaTarget` e extensões de terceiros | Fora do protocolo específico; seguem o comportamento original e os snapshots normais |

As preferências locais existentes são mantidas nos objetos ativos. Não há armazenamento adicional de preferências entre sessões; ao reconectar, o carregamento inicial do cenário pode restabelecer defaults de visualização.

## Diagnóstico

Procure `[CNC-LMP]` em `KSP.log` e nos logs do servidor. Estados distintos: `requested`, `accepted`, `saved-and-broadcast`, `received-awaiting-scene`, `applied`, `client-reports-applied`, `rejected` e `confirmation-timeout-retry`. `client-reports-applied` é a declaração do cliente; não substitui o teste físico de comunicação.

- `stale-revision`: estado mudou enquanto a edição estava em andamento; revise e repita a edição.
- `vessel-lock-required`: falta controle/update/unloaded-update, ou outro jogador tem controle.
- `vessel-topology-changed`/`unknown-antenna`: as peças mudaram ou o servidor ainda não recebeu a nave; aguarde o snapshot normal e repita.
- `application-failed`: confira versão dos mods, existência das estações e corpos celestes; a conexão é encerrada.
- `System.Runtime.Serialization` ausente ou aviso de patch obrigatório com a 0.1.0: atualize cliente e servidor para 0.1.2 e reinicie ambos. O cliente usa o `JsonFx.dll` que já acompanha o Luna 0.29.2; não instale DLLs de sistema avulsas.
- `disabled`: confira migração, `settings.json` e integridade do backup no servidor.

Logs não registram senhas ou tokens. Nenhum handler de rede chama APIs Unity diretamente.

A 0.1.2 registra `status=performance` a cada 15 segundos enquanto conectado. `updateAvgMs` e `updateMaxMs` medem o tempo gasto em `Update` do bridge, incluindo captura/aplicação de dados; não medem renderização nem o tempo total do frame. Esses valores ajudam a verificar se o bridge ainda está consumindo tempo excessivo durante a sessão.

## Limites desta versão

- Multiplayer real, atualização visual de diálogos/mapa, controles de sonda, reconstrução de componentes após reconexão, geometria de estações e docking ainda precisam de validação em KSP. Compilação e testes do protocolo não comprovam esses comportamentos.
- Upgrades de estação em carreira são bloqueados, pois o custo financeiro precisa de uma transação conjunta com o Luna. O alvo desta versão é Sandbox. Alterações de nível fora do KSC são implementadas para Sandbox/ciência.
- Criação/exclusão de objetos de estação não é implementada; altere as estações já existentes. Todos os clientes precisam dos mesmos mods e definições de estações.
- Mudanças passam por uma operação pendente por cliente; aguarde a confirmação antes da próxima edição. O catálogo/estações é observado a cada 250 ms. Antenas usam também os pontos de mutação do Constellation; mudanças diretas de campos por terceiros são sobrepostas pelo estado aceito.
- A identidade física de partes é preservada na reconciliação de topologia, mas metadados de naves removidas não são coletados nesta versão. O limite é 4.096 registros de nave, 2.048 redes, 512 estações e 4 MiB por mensagem; universos maiores precisam de uma próxima revisão do protocolo.
- O formato texto aceito usa chaves estruturais em linhas próprias, conforme os arquivos KSP encontrados. Migração de arquivos malformados ou variantes diferentes falha explicitamente.

## Desinstalação

Com os jogos e servidor encerrados, remova a DLL/pasta do bridge dos clientes e do servidor. Preserve `Universe/CommNetLunaBridge` para recuperação. A projeção CNC permite carregamento pelo mod original, mas o Luna original volta ao comportamento sem sincronização ao vivo. Para reverter exatamente ao estado anterior, restaure o backup completo do universo; o `migration-backup` contém somente os arquivos de cenário CNC e naves lidos pela migração.

## Fonte, compilação e testes

Código sob GPL-3.0; veja `LICENSE` e [dependências/licenças](docs/DEPENDENCIAS.md). O pacote inclui fonte correspondente. Assemblies proprietários do KSP não são redistribuídos.

Na pasta `Source` do pacote, use SDK .NET 6 e os caminhos de suas instalações:

```powershell
& .\build.ps1 -KspRoot 'D:\KSP-teste' -LmpServerRoot 'D:\LMP-teste'
```

O script compila cliente `net472`, servidor e migrador `net6.0`, executa testes do núcleo, do plugin, da ABI e de JSON (inclusive no Mono do KSP em processo separado no Windows). Instale o SDK .NET 6 para compilar. Informe os caminhos pelos argumentos ou pelas variáveis `KSP_ROOT` e `LMP_SERVER_ROOT`; não existem caminhos pessoais configurados no código. Testes do plugin usam clientes sintéticos e filas/fábricas/locks reais das DLLs Luna, em diretórios temporários e sockets loopback, sem iniciar sessões KSP ou o servidor de uso.

Resultados e distinção entre evidências estão em [RELATORIO.md](docs/RELATORIO.md).

Para executar somente os testes independentes de KSP/Luna:

```powershell
dotnet run --project tests/Tests.csproj -c Release
dotnet build src/Migrate/Migrate.csproj -c Release
```

O CI verifica esse núcleo e o migrador usando apenas dados sintéticos. Cliente, servidor e ABI precisam de suas próprias instalações legítimas; os assemblies de terceiros não são fornecidos no repositório.

## Download e contribuições

Use a seção **Releases** deste repositório para baixar `CommNetLunaBridge-0.1.2-experimental.zip` e seu SHA-256. O ZIP contém cliente, plugin de servidor, migrador, documentação, licenças e fonte correspondente. A versão é uma pré-release experimental; consulte [CHANGELOG.md](CHANGELOG.md), [CONTRIBUTING.md](CONTRIBUTING.md) e [a revisão da distribuição](docs/DISTRIBUICAO.md).

Projeto independente, sem afiliação ou endosso dos autores de KSP, Constellation ou Luna Multiplayer. Os nomes desses projetos identificam as dependências e a compatibilidade pretendida.
