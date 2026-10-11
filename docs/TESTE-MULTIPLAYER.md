# Validação real pendente — dois clientes KSP

Execute em um servidor de teste com cópia do universo e dois clientes KSP com os mesmos mods. Instale o bridge nos dois clientes e no servidor de teste, faça migração offline e inclua A e B em `Editors`. Dê nomes diferentes aos jogadores. Registre revisão, ação, estado visível, estado físico e arquivos/logs antes/depois. Não use o universo de uso para introduzir perdas/duplicações deliberadas.

**Esta tabela é um roteiro; os casos abaixo não foram executados em KSP.**

| Caso | Procedimento e critério |
|---|---|
| Criar ao vivo | A cria `Rede Kerbin` em 100 com B conectado e painel aberto. B deve ver a rede sem mudar de cena. Compare revisão em ambos e no servidor. |
| Nome/cor | A edita a rede; mantenha os painéis e mapa abertos nos dois clientes. Linhas, nomes e cores devem mudar e as preferências de visibilidade de B devem permanecer locais. |
| Frequência | A muda 100 para 101. ID da rede deve permanecer; antenas, estação e listas manuais afetadas devem convergir. Nenhuma nave de B pode disputar um proto antigo para restaurar 100. |
| Excluir | A exclui a rede. Referências afetadas devem passar para a pública; lista manual não deve duplicar a mesma frequência. Exclusão/renumeração da pública deve ser recusada. |
| Física da comunicação | Crie uma sonda sem outro caminho de comunicação. Configure sua antena numa frequência exclusiva e deixe o destino em outra. Confirme bloqueio real de comando/transmissão nos dois clientes; faça as frequências coincidirem e confirme recuperação. Aparência de linha não é suficiente. |
| Estação | Mude frequências, cor, nome e nível de uma estação não KSC. Confirme potência/ligação/controle. Teste coordenadas e corpo, incluindo desativar override em estação stock; confirme ausência de nós duplicados. |
| Antena distante | A adquire lock de nave não carregada e muda frequência/uso/nome. B observa, depois carrega a nave e troca de cena. Estado e comunicação devem permanecer corretos. |
| Ownership | Com B controlando a nave, A tenta editar antenas, inclusive possuindo um update lock antigo. Servidor deve recusar. Depois libere o controle e teste com o lock correto. |
| Listas | Alterne AutoBuild/LockList; edite lista manual; deploie/retraia antenas. Potência de AutoBuild deve continuar dinâmica; remap da lista manual deve persistir. |
| Conflito | A e B tentam criar a mesma frequência simultaneamente. Uma operação deve ser aceita e a outra recusada/ressincronizada; não deve haver duplicação. |
| Dados antigos | Atrase mensagens de um cliente ou reconecte a partir de cache antigo. Rede nova não pode desaparecer. Verifique também recriação de nave por proto antigo sem gerar uma edição local automática. |
| Entrada tardia | C entra depois das mudanças. Deve receber o snapshot atual antes de poder editar. |
| Reconexão | B desconecta/reconecta pelo menos cinco vezes, alternando flight/tracking/editor/space center. Confira CNC ativo, ausência de rede padrão substituindo CNC e nenhuma multiplicação de componentes/handlers. |
| Reinício | Encerre servidor de teste após confirmação de uma edição, reinicie e reconecte. Catálogo, estações e antenas aceitos devem reaparecer. |
| Duplicação/perda | Num teste controlado, repita a mesma operação/ID e atrase sua confirmação por mais de cinco segundos. Deve ocorrer retry sem revisão extra; snapshot de resync deve recuperar o estado. |
| Topologia | Docking, undocking, staging e remoção de nave com antenas editadas. Frequências devem seguir a parte física (`uid`) e convergir após novos snapshots. Registre qualquer conflito/limitação. |
| Ausência/versão | Entre sem bridge ou com versão diferente. Deve ocorrer desconexão explícita; não permita gravação silenciosa de CNC por esse cliente. |
| Single-player | Inicie save single-player com bridge instalado. Criação/edição/antenas do Constellation devem manter comportamento original, sem solicitações de rede. |

Colete `KSP.log` de A/B, logs `[CNC-LMP]` do servidor e cópia de `Universe/CommNetLunaBridge/state.json` após as confirmações. Examine o cenário projetado e protos normais de nave, lembrando que o estado dedicado é a autoridade. Compare estado ativo com persistido. Teste reconectar somente B, mantendo servidor e A ligados, antes de testar reinício do servidor.

Qualquer falha física, divergência de revisão, loop de operações ou substituição da rede invalida a promoção para o servidor de uso. Registre a reprodução e corrija antes de concluir compatibilidade.

## Regressões obrigatórias da 0.1.3

1. Nave criada antes do CNC: editar nome/frequência de antena e confirmar accepted nos dois lados. Sem unknown-antenna para rádio existente.
2. Confirmar valor no segundo cliente e após reconectar/reiniciar. Peça/índice falso ou falta de lock devem rejeitar; cliente restaura valor anterior e registra restored-after-rejection.
3. Abrir ferramenta, mudar revisão por outro cliente e tentar salvar rascunho antigo: exigir reabertura/atualização.
4. Carreira: upgrade com/sem saldo, máximo/KSC, retry/reconexão. Cliente não cobra antes do aceite. Conferir coexistência com lançamento/contrato e dois upgrades concorrentes.
5. Interromper transação preparada somente em universo descartável. Reiniciar deve completar nível/recibo/Funding antes do handshake. Não apagar journal de universo em uso.
6. Visibilidade/modos/ocultação/estilo: sair/entrar e conferir persistência apenas no cliente que alterou.
7. LockList + staging/docking/undocking: conferir lista/potência sobrevivente, identidade física e segundo cliente. Recuperar/destruir nave: limpeza após 30 s ausente no store.
8. Custos/potências ou lista de estações divergentes: conexão bloqueada com diagnóstico. Igualar configurações CNC/planetas e settings.json antes de repetir.
