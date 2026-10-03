# Publicar no GitHub

Passo a passo para pôr o NexusGuard num repositório e distribuir atualizações a partir dele.

---

## 1. Criar o repositório

O `gh` (CLI do GitHub) não está instalado nesta máquina. Duas opções:

**Pelo site** — <https://github.com/new>, nome `NexusGuard`, **sem** README/`.gitignore`/licença
(o repositório local já os tem). Depois:

```powershell
cd C:\Users\Lapa\Documents\APPlimpezawindows
git branch -M main
git remote add origin https://github.com/SEU-UTILIZADOR/NexusGuard.git
git push -u origin main
```

**Pelo CLI** — `winget install GitHub.cli`, depois:

```powershell
cd C:\Users\Lapa\Documents\APPlimpezawindows
gh auth login
git branch -M main
gh repo create NexusGuard --source . --public --push
```

### Público ou privado?

**O repositório tem de ser público para as atualizações funcionarem.** A aplicação consulta a API
do GitHub sem qualquer credencial; num repositório privado todos os pedidos devolvem 404 e a única
alternativa seria embutir um token no executável — o que o entregaria a quem o instalasse.

Se o código não puder ser público, a saída é separar as coisas: o código num repositório privado e
só os ficheiros do release num repositório público à parte, mudando o `Repo` em
`Modules/Updater.cs`.

### Licença

O repositório não inclui licença. **Sem ficheiro de licença, código público continua a ser «todos
os direitos reservados»** — qualquer pessoa pode ver, ninguém pode usar nem redistribuir
legalmente. Se a intenção é vender, é isso mesmo que se quer. Se a intenção é deixar outros
contribuir, é preciso escolher uma licença, e essa decisão é sua.

---

## 2. Apontar a aplicação para o repositório

Em `src/NexusGuard/Modules/Updater.cs`:

```csharp
public const string Owner = "SEU-UTILIZADOR";   // ← troque pelo seu nome de usuário
public const string Repo  = "NexusGuard";
```

Enquanto o `Owner` começar por `SEU-`, a aplicação sabe que não está configurada: a secção de
atualizações aparece desligada em vez de dar erros de rede.

---

## 3. Publicar uma versão

```powershell
git tag v1.1.0
git push origin v1.1.0
```

A etiqueta dispara o `.github/workflows/release.yml`, que no GitHub:

1. instala o .NET 10 e garante o Inno Setup;
2. corre `build.ps1 -SelfContained -Installer -Version 1.1.0`;
3. calcula o `SHA256SUMS.txt`;
4. cria o release com o instalador e o ficheiro de somas anexados.

A versão vem da etiqueta e entra no executável, no instalador e no nome do ficheiro — não há nada
para editar à mão.

Para experimentar sem criar etiqueta: separador **Actions** → **Release** → **Run workflow**,
indicando a versão.

---

## 4. Como a atualização chega ao usuário

Em **Configurações → Atualizações**:

- **Procurar ao iniciar** (ligado) consulta a API uma vez no arranque. Havendo versão nova, surge
  uma notificação na bandeja e um emblema em Configurações. Nada é baixado sem confirmação.
- **Procurar agora** faz a mesma consulta a pedido e mostra as notas do release.
- **Baixar e instalar** descarrega o instalador, confere o SHA-256 contra o `SHA256SUMS.txt`
  publicado, corre o setup em modo silencioso e fecha a aplicação para o executável poder ser
  substituído.
- O **canal beta**, em Avançado, faz a procura incluir pré-lançamentos.

Instaladores baixados são apagados ao fim de 7 dias.

### O que a verificação de hash garante — e o que não garante

Confere que o ficheiro recebido é byte a byte o que o release publica, o que apanha um download
corrompido ou interrompido. **Não** prova que o release é legítimo: quem controlar o repositório
publica o instalador e o hash. Só a assinatura de código resolve isso, e o executável continua por
assinar. Está escrito na própria página de Configurações, para não dar uma falsa sensação de
segurança.

---

## 5. O que o SmartScreen vai fazer

Sem certificado de assinatura, o Windows avisa nas primeiras instalações: «O Windows protegeu o seu
PC» → **Mais informações** → **Executar assim mesmo**. O aviso desaparece sozinho ao fim de algum
tempo e de downloads suficientes, ou imediatamente com um certificado EV, que tem de ser comprado a
uma autoridade certificadora.

As notas de cada release já explicam isto a quem descarregar.
