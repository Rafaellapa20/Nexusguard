# Componentes de terceiros

O NexusGuard distribui, dentro do seu executável, os componentes abaixo. Esta página existe para
cumprir as licenças deles — em especial a MPL-2.0, que obriga a dizer onde está o código-fonte de
cada componente coberto.

Nenhum destes ficheiros foi alterado. São usados tal como os autores os publicam, pela versão
indicada.

---

## LibreHardwareMonitorLib 0.9.6

Leitura dos sensores de hardware: temperaturas, ventoinhas, relógios, memória das placas gráficas
e dados S.M.A.R.T. dos discos.

- Licença: **Mozilla Public License 2.0**
- Código-fonte: <https://github.com/LibreHardwareMonitor/LibreHardwareMonitor>
- Texto da licença: <https://mozilla.org/MPL/2.0/>

A MPL-2.0 é um copyleft por ficheiro: permite viver dentro de uma aplicação proprietária desde que
os ficheiros cobertos não sejam modificados, e obriga a publicar as alterações a esses ficheiros
caso alguma seja feita. O NexusGuard não altera nenhum.

### Componentes que entram com ele

| Componente | Versão | Licença | Código-fonte |
|---|---|---|---|
| DiskInfoToolkit | 1.1.2 | MPL-2.0 | <https://github.com/Blacktempel/DiskInfoToolkit> |
| RAMSPDToolkit-NDD | 1.4.2 | MPL-2.0 | <https://github.com/Blacktempel/RAMSPDToolkit> |
| HidSharp | 2.6.4 | Apache-2.0 | <https://software.seekye.com/hidsharp> |
| Mono.Posix.NETStandard | 1.0.0 | MIT | <https://github.com/mono/mono> |
| System.IO.Ports | 10.0.3 | MIT | <https://github.com/dotnet/runtime> |
| System.Management | 10.0.2 | MIT | <https://github.com/dotnet/runtime> |
| System.IO.FileSystem.AccessControl | 5.0.0 | MIT | <https://github.com/dotnet/runtime> |

---

## Trabalho consultado, não distribuído

Estes projectos não entram no executável. Aparecem aqui porque influenciaram decisões de
implementação e é honesto registá-lo.

### CrystalDiskInfo — MIT

<https://github.com/hiyohiyo/CrystalDiskInfo>

A forma de apresentar a saúde de um disco — vida restante em percentagem, blocos de reserva,
horas ligado, total escrito, e o limite declarado pelo próprio disco como critério de alarme —
segue o que o CrystalDiskInfo estabeleceu. O código é C++ com interface própria e não foi
incorporado; os valores vêm do `DiskInfoToolkit` listado acima, que é derivado dele.

### UniGetUI — MIT

<https://github.com/Devolutions/UniGetUI>

A forma de tratar vários gestores de pacotes sob a mesma interface, e os argumentos de linha de
comando de cada um, seguem o que o UniGetUI faz. O NexusGuard invoca os gestores directamente, sem
incorporar código do projecto.

---

## Projecto deliberadamente não usado

### BleachBit — GPL-3.0

<https://github.com/bleachbit/bleachbit>

O BleachBit tem a maior colecção pública de regras de limpeza por aplicação, e seria o caminho
óbvio para alargar a limpeza do NexusGuard. **Não é usado, nem o código nem as definições de
limpeza.**

A razão é a licença. A GPL-3.0 obriga qualquer programa que incorpore código coberto a ser também
distribuído sob a GPL-3.0, com o código-fonte disponível a quem receba o binário. Isso é
incompatível com distribuir o NexusGuard como produto fechado. As definições de limpeza em
`cleaners/*.xml` fazem parte do mesmo projecto e estão sob a mesma licença, pelo que copiá-las — ou
transcrever os caminhos que elas listam — teria o mesmo efeito.

As regras de limpeza por aplicação do NexusGuard são escritas de novo, a partir da documentação de
cada aplicação e de observação directa das pastas que ela cria.
