# Cobertura do Constellation 1.5.7 — Bridge 0.1.3

Inventário baseado no código da versão fixada e nos métodos/campos da DLL instalada. Abrange as funções identificadas nessa versão. “Implementada” indica um caminho de código e a evidência indicada; não significa certificação de UI, física ou interoperabilidade em dois clientes KSP. Nenhum percentual de funcionamento no jogo foi medido.

| Função disponível | Tratamento no bridge | Evidência / limite |
|---|---|---|
| Criar constelação | Create, revisão e permissão | Núcleo e filas reais Luna |
| Nome/cor de constelação | Edit; painel atualizado | Núcleo; UI real a validar |
| Mudar frequência com identidade preservada | Remap de redes/estações/antenas/listas | Núcleo e conflitos |
| Excluir constelação | Remap para pública; pública protegida | Núcleo |
| Restaurar nome/cor da pública | UI original produz edit | Guarda/ABI; UI a validar |
| Nome/cor/frequências de estação | Station validado | Núcleo; estação existente obrigatória |
| Upgrade de estação 0–3 em sandbox/ciência | Upgrade, sem cobrança | Núcleo |
| Upgrade de estação em carreira | Custo/saldo no servidor, journal, recibo, mensagem Luna de fundos | Núcleo/plugin; economia simultânea real a validar |
| Nível/potência do KSC | Instalações KSP/Luna originais | CNC não pode cobrar/alterar pelo editor de estação |
| Corpo/coordenadas por configuração CNC | Importação, persistência e aplicação direta | Núcleo/inspeção; geometria/restauração a validar |
| Nome/frequência no painel de antena | Guardas e operação vessel | ABI; aplicação em peça carregada e proto |
| Frequência independente por antena | IndepAntennaFreqTool + updateFrequency | Guardas de lock/revisão; teste vessel |
| Seleção individual/todas de antenas | AntennaSelectionTool + toggleAntenna | Guardas de lock/revisão; teste vessel |
| Frequência única e retorno à pública | VanillaFreqTool | Guardas de lock/revisão; teste vessel |
| AutoBuild | Configuração compartilhada; potência pelo CNC | Não congela potência dinâmica; teste overlay |
| LockList | Frequências/potências persistidas e aplicadas | Núcleo; exceção de staging com topologia nova |
| Naves antigas/não carregadas | Registro por transmissores/controle de experimento; overlay CNC | Testes MM e cópia do universo fornecido |
| Lançamento/docking/undocking/staging | Protos Luna; transferência por flightID; revisão de topologia | Núcleo; sequências físicas no KSP pendentes |
| Extensão/retração, consumo, ciência transmitida | Simulação CNC e física/recursos Luna originais | Sessão real pendente |
| Experimentos de superfície | ModuleGroundExpControl reconhecido; comportamento CNC/Luna conservado | Registro testado; eventos reais pendentes |
| Visibilidade de rede, linhas de voo/Tracking Station | Preferências locais por identidade de rede | Persistência testada; cenas/UI a validar |
| Ocultar estações/estilo antigo de órbita | Preferências locais | Persistência testada; sem transmissão |
| Painel/foco/mapa/telemetria/cores/filtros | UI CNC original; atualização após snapshot | ABI; interação real pendente |
| Antena no editor de nave | Craft local; registro no lançamento Luna | Guarda permite editor sincronizado; lançamento pendente |
| Reconexão | Reset de sessão/snapshot; preferências conservadas | Código/inspeção; sessão real 0.1.3 pendente |
| Retomar servidor | Estado durável/backup/recibos/recuperação financeira | Núcleo/plugin |
| Nave recuperada/destruída/removida | Limpeza após ausência no store Luna | Núcleo; janela de 30 s para transferir peças |
| Single-player | Guardas inativas com Luna desconectado | Inspeção; sessão real a validar |

## Itens que não são funções disponíveis nessa versão

A DLL instalada expõe somente AutoBuild e LockList. UpdateOnly está comentado no código, incluindo enum/UI; o teste ABI confirma que não existe no binário. AntennaTarget é um TODO sem direcionamento implementado. CosAngle é preservado nos protos existentes; não foi identificado controle funcional de cone/direcionamento no CNC 1.5.7. Criação/exclusão livre de estações não existe na UI instalada: novas estações exigem configuração física compatível nos clientes e migração offline da lista. Nenhum desses itens é anunciado como recurso do bridge.

## Verificação desta atualização

- 62 testes de núcleo/migração locais; 61 com dados sintéticos.
- 24 verificações do plugin com APIs/fábricas/filas/locks das DLLs Luna instaladas; 23 sintéticas.
- Auditoria em cópia temporária: 30 arquivos de nave, 18 naves com rádio, 18 edições aceitas. Partida do state.json existente; origem somente lida.
- 49 vínculos/enum conferidos nas DLLs CNC/KSP/Luna instaladas.
- 18 verificações de JSON cliente/servidor e 16 no Mono do KSP em processo separado.
- Cliente/servidor/migrador compilados sem avisos/erros.

O teste reproduz uma nave legada no plugin e verifica aceite sem snapshot CNC prévio. Falta confirmar a 0.1.3 no KSP após a troca das DLLs. Os números não representam dois jogadores executando Unity/KSP.

## Pontas que exigem validação externa

[TESTE-MULTIPLAYER.md](TESTE-MULTIPLAYER.md) cobre dois clientes, bloqueio/alcance físico, geometria de estação, staging/docking, cenas, recursos e reconexão. Em carreira, testar upgrades concorrentes e coexistência com contratos/lançamentos: o saldo absoluto é compartilhado pelo mecanismo Luna original. Mods que alteram MM/peças/planetas precisam da mesma configuração nos clientes. O bridge confere versão, frequência pública, custos/potências e lista de estações; não valida toda a base física de mods. Esses limites impedem afirmar “100% garantido” com a evidência atual.
