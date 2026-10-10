# Dependências e licenças

| Componente | Versão/referência | Uso |
|---|---|---|
| KSP | 1.12.5.3190 local | Referências de cliente; proprietário, não incluído |
| CommNet Constellation | Arquivo 1.5.7 / assembly 1.5.0.0 | API cliente; GPL-3.0, não incluído como mod |
| Luna | Cliente e servidor 0.29.2.0 locais | Transporte, plugin, locks e cenários; MIT |
| Harmony | 2.2.1.0 já instalado | Patches cliente; MIT, não incluído |
| LunaConfigNode | 1.9.1.0 já presente no servidor | Projeção de cenário; não incluído |
| Microsoft.NETFramework.ReferenceAssemblies | NuGet 1.0.3 | Somente build; assemblies de referência Microsoft |
| SDK .NET | 6.0.428 portátil local | Compilação; não incluído no pacote |
| Apphost .NET | 6.0.36, gerado pelo SDK | Inicializador nativo do executável do migrador; avisos .NET/Microsoft em `licenses/` |
| ILSpy CLI | 8.2.0.7535 | Inspeção local dos binários; não é dependência de execução |
| LunaCompat | Fonte tag 0.1.0 | Referência de APIs; MIT; não é dependência de execução |

O código do bridge é disponibilizado sob **GPL-3.0**, com fonte correspondente e texto integral em `LICENSE`. A escolha mantém a distribuição do complemento que referencia a API GPL do Constellation sob a mesma licença. Nenhum assembly proprietário do KSP ou arquivo de jogo integra o ZIP.

Fontes e textos originais: [Constellation/LICENSE](https://github.com/KSP-TaxiService/CommNetConstellation/blob/b2770492bbf2c0c2482aa2e4ba63dde0555c824b/LICENSE), [Luna/LICENSE](https://github.com/LunaMultiplayer/LunaMultiplayer/blob/60a2ed5dbfdddc08536a98b999c781e3c12b0649/LICENSE), [LunaCompat/LICENSE](https://github.com/TheXankriegor/LunaCompat/blob/2d60578af51ed2e6e9bb29b305f6ca7015c99818/LICENSE), [Harmony 2.2.1/LICENSE](https://github.com/pardeike/Harmony/blob/v2.2.1.0/LICENSE). Cópias MIT de Luna, LunaCompat e Harmony estão em `licenses/` para atribuição. O bridge usa implementação própria de protocolo/estado; não incorpora a biblioteca LunaCompat.

Os SHAs das fontes públicas consultadas estão no relatório. O inventário da instalação local não é distribuído. Informe `KspRoot`/`LmpServerRoot` na compilação; não há defaults pessoais. Apenas assemblies gerenciados do próprio bridge e do migrador integram o ZIP, acompanhados pelo fonte correspondente. O executável Windows do migrador contém o inicializador padrão (apphost) do .NET; os avisos MIT do runtime .NET e de terceiros foram incluídos em `licenses/`. O runtime .NET completo não é empacotado. Referências oficiais: [licença .NET 6.0.36](https://github.com/dotnet/runtime/blob/v6.0.36/LICENSE.TXT) e [avisos .NET](https://github.com/dotnet/runtime/blob/v6.0.36/THIRD-PARTY-NOTICES.TXT).
