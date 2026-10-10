# Relatório — 10/10/2026

Foi implementado um bridge experimental de cliente e plugin de servidor. Este repositório contém uma exportação revisada para distribuição, com histórico novo e dados pessoais removidos. A instalação e o universo usados no desenvolvimento não foram modificados.

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
| Dois clientes KSP / física / UI / cenas | Não executado | Não há compatibilidade multiplayer comprovada |

Os logs brutos da máquina não são distribuídos. O CI público executa 42 testes sintéticos; o teste adicional local de leitura do universo explica o total de 43 acima. A reprodução dos testes do plugin exige um universo adequado de teste e as DLLs Luna indicadas; eles incluem a frequência sintética 22 e pelo menos uma nave CNC com antena. A exportação pública foi recompilada e os 43 testes de núcleo, 13 verificações do plugin e 36 vínculos ABI foram conferidos novamente antes da publicação. Os testes de plugin não enviam comandos ao servidor de uso e não representam autenticação/transportes de uma sessão KSP. A migração mantém backups com hashes SHA-256.

## Pendências e limites

Executar todos os casos de [TESTE-MULTIPLAYER.md](TESTE-MULTIPLAYER.md) em cópias isoladas. Especialmente: bloqueio físico de comunicação por frequência, UI aberta, naves não carregadas, atrasos entre canais do Luna, reconstrução durante docking, geometria de estações, reconexões repetidas e reinstanciação correta da rede. Upgrades de estações em carreira ficam bloqueados nesta versão. Criação/exclusão de estações, coleta de metadados de naves removidas e persistência adicional de preferências locais não foram implementadas. Os demais limites estão no README.

O pacote é revisável e compilável. A implementação ainda não pode ser descrita como integração plenamente validada no jogo.
