# Relatório de atualização — 0.1.3

Corrigido o registro de antenas anteriores à instalação do CNC. O servidor deriva módulos das peças do universo confiável; o cliente não pode inventar a peça/índice. A auditoria em cópia temporária do estado existente encontrou 18 naves com rádio entre 30 arquivos e confirmou edição nas 18. A origem foi preservada.

Acrescentados upgrades em carreira com transação recuperável, validação de custos/potências, rollback de rejeições, guardas para ferramentas em revisão antiga, preferências locais, limpeza de metadados e reconstrução de listas travadas com topologia nova. Detalhes em [COBERTURA-CONSTELLATION.md](COBERTURA-CONSTELLATION.md).

Verificação: 62 testes de núcleo/migração, 24 do plugin, 49 vínculos ABI, 18 de JSON e 16 no Mono do KSP; compilação sem avisos/erros. O plugin usa clientes sintéticos. A auditoria lê a origem e edita cópias temporárias. Dados privados não integram fonte/pacote.

A sessão anterior confirmou a melhora de desempenho da 0.1.2 e rejeições unknown-antenna. A 0.1.3 precisa de confirmação de UI/física no jogo, especialmente dois clientes e economia de carreira junto de contratos/lançamentos. A implementação cobre os caminhos identificados no CNC 1.5.7; a evidência atual não permite garantir 100% de funcionamento.

---

## Histórico técnico até a 0.1.2

# Relatório — 10/10/2026

Foi implementado um bridge experimental de cliente e plugin de servidor. Este repositório contém uma exportação revisada para distribuição, com histórico inicial novo e dados pessoais removidos. O universo usado no desenvolvimento permanece privado.

## Diagnóstico e evidência

Versões verificadas em metadados e no log local: KSP 1.12.5.3190; Luna cliente/servidor 0.29.2.0; Constellation arquivo 1.5.7, assembly 1.5.0.0; Harmony 2.2.1.0. O inventário de caminhos e hashes da instalação, os saves e o universo usados no desenvolvimento permanecem privados.

A inspeção das DLLs instaladas, por ILSpy 8.2.0.7535, confirmou: `Server.System.ScenarioSystem.ParseReceivedScenarioData` encaminha snapshots ao armazenamento sem broadcast nesse caminho; `LmpClient.Systems.Scenario.ScenarioSystem.LoadScenarioDataIntoGame` acrescenta módulos proto ao jogo, sem aplicar atualizações aos objetos CNC ativos. Não há `SendScenarioModuleImmediate` nesse cliente instalado. Isso confirma a lacuna estática de sincronização, **não uma reprodução da causa do incidente relatado**. Nenhuma sessão KSP com dois jogadores foi executada; não há correlação temporal de logs de criação/edição do incidente.

Fontes públicas consultadas e fixadas localmente: [Luna 0.29.2](https://github.com/LunaMultiplayer/LunaMultiplayer/tree/60a2ed5dbfdddc08536a98b999c781e3c12b0649), [Constellation v1.5.7](https://github.com/KSP-TaxiService/CommNetConstellation/tree/b2770492bbf2c0c2482aa2e4ba63dde0555c824b), [LunaCompat 0.1.0](https://github.com/TheXankriegor/LunaCompat/tree/2d60578af51ed2e6e9bb29b305f6ca7015c99818). A versão numérica de um assembly não identifica sozinha seu commit; os binários locais foram usados diretamente nas referências de compilação e testes de ABI, sem presumir equivalência exata com o HEAD público.

## Alterações

Servidor com autoridade, revisão/época, validação, deduplicação persistente, operação de remap para catálogo/estações/antenas/listas manuais, escrita atômica e backup. Canal confiável próprio sobre o transporte de mods Luna. Clientes sem handshake obrigatório não podem editar CNC e são desconectados. Cenários CNC antigos são filtrados em lotes, preservando cenários independentes; protos antigos recebem os campos CNC oficiais antes do caminho normal de nave.

Cliente aplica dados em `Update`, mantém identidade das redes, atualiza arrays de estação e o cálculo de antenas, cobre peças/protopeças sem carregar todas as naves e atualiza o painel de controle aberto. Guardas Harmony bloqueiam edições sem sincronização/lock. Dados de nave recriados pelo Luna não são confundidos com comandos locais: somente pontos de mutação do Constellation marcam uma edição de antena. O cenário padrão é ignorado no recebimento e na criação de módulos faltantes durante multiplayer. Nada chama novamente `CNCCommNetScenario.Start` para aplicar revisões.

## Verificação executada

| Evidência | Resultado | Alcance |
|---|---|---|
| Compilação cliente, servidor, migrador | Sucesso; 0 erros, 0 avisos | Assemblies reais locais |
| Testes de núcleo/migração | 43 passaram | Serialização, validação, identidade, remaps, dedup, revisão, backup, topologia e leitura do universo real |
| Integração do plugin | 13 passaram | Fábricas, filas, hooks, locks e store reais do Luna; clientes sintéticos |
| ABI cliente | 36 vínculos conferidos | Métodos/campos/nomes de parâmetros das DLLs instaladas; sem executar Unity |
| JSON cliente/servidor 0.1.2 | 18 verificações passaram | Handshake, Unicode, números, dados aninhados e rejeição de entrada inválida; compatibilidade com o serializador do servidor |
| JSON no Mono instalado com KSP | 16 verificações passaram | Runtime real do KSP em processo separado, sem iniciar Unity ou uma sessão de jogo |
| Dois clientes KSP / física / UI / cenas | Não executado | Não há compatibilidade multiplayer comprovada |

Os logs brutos da máquina não são distribuídos. O CI público executa 42 testes sintéticos; o teste adicional local de leitura do universo explica o total de 43 acima. A reprodução dos testes do plugin exige um universo adequado de teste e as DLLs Luna indicadas; eles incluem a frequência sintética 22 e pelo menos uma nave CNC com antena. A exportação pública foi recompilada e os 43 testes de núcleo, 13 verificações do plugin e 36 vínculos ABI foram conferidos novamente antes da publicação. Os testes de plugin não enviam comandos ao servidor de uso e não representam autenticação/transportes de uma sessão KSP. A migração mantém backups com hashes SHA-256.


A 0.1.2 corrige custo de polling: comparações de catálogo/estações/naves passam a comparar valores diretamente. Naves ainda sem registro oficial recebem baseline uma vez, naves inalteradas não são capturadas duas vezes, e a aplicação só chama OnAntennaChange quando há mudança de dados aplicáveis ou força explícita de estado/cena. Em benchmark sintético no Mono instalado com KSP (30 registros de nave, 9 estações, 2 redes), a comparação antiga custou 70,762 ms por ciclo e a direta 0,003 ms. O benchmark inclui somente comparação, não captura de objetos Unity, renderização ou FPS. Para reproduzir: execute MonoHost com os argumentos de caminho usados em build.ps1, acrescentando --benchmark. A melhora dentro de uma sessão KSP ainda precisa ser medida.

## Pendências e limites

A primeira tentativa no KSP mostrou que a 0.1.0 carregava, mas falhava ao enviar o handshake por ausência de `System.Runtime.Serialization` no perfil Mono do jogo. O servidor encerrava a conexão ao expirar o prazo. A 0.1.1 usa o JsonFx já instalado pelo Luna no cliente, mantendo o serializador e o formato persistido do servidor. Os testes acima verificam a correção no runtime; a conexão e as mudanças de cena ainda precisam ser confirmadas após reiniciar o jogo com a atualização. Os logs privados não integram o repositório.

Executar todos os casos de [TESTE-MULTIPLAYER.md](TESTE-MULTIPLAYER.md) em cópias isoladas. Especialmente: bloqueio físico de comunicação por frequência, UI aberta, naves não carregadas, atrasos entre canais do Luna, reconstrução durante docking, geometria de estações, reconexões repetidas e reinstanciação correta da rede. Até a 0.1.2, upgrades em carreira, limpeza de metadados e persistência adicional de preferências eram pendentes. A 0.1.3 implementa esses caminhos. Criação/exclusão livre de estações não existe na UI CNC 1.5.7. Os demais limites estão no README.

O pacote é revisável e compilável. A implementação ainda não pode ser descrita como integração plenamente validada no jogo.
