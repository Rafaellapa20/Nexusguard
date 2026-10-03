# Publicar no GitHub

Como o NexusGuard chega ao repositório e distribui atualizações a partir dele.

Repositório: <https://github.com/Rafaellapa20/Nexusguard> — público, ramo `main`.

---

## 1. Enviar o código

O remoto local já está configurado. Para enviar:

```powershell
cd C:\Users\Lapa\Documents\APPlimpezawindows
git push -u origin main
```

### Porque é público

A aplicação consulta a API do GitHub sem qualquer credencial. Num repositório privado todos os
pedidos devolveriam 404, e a única alternativa seria embutir um token no executável — que ficaria
nas mãos de quem o instalasse. Sendo público, as atualizações funcionam sem nada disso.

### Licença

O repositório não inclui licença. **Sem ficheiro de licença, código público continua a ser «todos
os direitos reservados»** — qualquer pessoa pode ver, ninguém pode usar nem redistribuir
legalmente. Se a intenção é vender, é isso mesmo que se quer. Se a intenção é deixar outros
contribuir, é preciso escolher uma licença, e essa decisão é sua.

---

## 2. Aplicação já apontada para o repositório

Em `src/NexusGuard/Modules/Updater.cs`:

```csharp
public const string Owner = "Rafaellapa20";
public const string Repo  = "Nexusguard";
```

Confirmado com um pedido real à API: a aplicação encontra o repositório e responde
«Ainda não há nenhuma versão publicada», que é o correcto enquanto não houver releases.

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

---

## 6. Email nos commits

Num repositório público, o email do autor fica visível em cada commit, para sempre. O GitHub dá um
endereço alternativo que não revela o pessoal:

```
223636372+Rafaellapa20@users.noreply.github.com
```

Para passar a usá-lo nos commits futuros:

```powershell
cd C:\Users\Lapa\Documents\APPlimpezawindows
git config user.email "223636372+Rafaellapa20@users.noreply.github.com"
git config user.name "Rafaellapa20"
```

Para reescrever também os commits já feitos — só possível **antes** do primeiro push:

```powershell
git filter-branch -f --env-filter "GIT_AUTHOR_EMAIL='223636372+Rafaellapa20@users.noreply.github.com'; GIT_COMMITTER_EMAIL='223636372+Rafaellapa20@users.noreply.github.com'" -- --all
```
