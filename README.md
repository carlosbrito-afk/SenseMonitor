# SenseMonitor

Central de controle leve para notebooks gamer da Acer (Nitro, Nitro V, Predator e afins), feita para
substituir o NitroSense / PredatorSense. Fica na bandeja do Windows e abre com a tecla NitroSense.

> Projeto independente. Não é afiliado, patrocinado nem aprovado pela Acer. "Acer", "Nitro",
> "NitroSense" e "Predator" são marcas dos seus donos e aparecem aqui só para dizer com quais
> notebooks o app é compatível.

## O que faz

- **Monitor:** temperatura e uso de CPU e GPU (gráficos de 10 minutos), rotação das ventoinhas, memória.
- **Limpar RAM** de verdade (esvazia working sets, lista modificada e cache em espera, como o RAMMap).
- **Modos:** Silencioso, Dia a dia e Desempenho, com conferência no firmware.
- **Ventoinha:** Automático, Manual por ventoinha, Curva editável e Máximo — liberados só depois de um
  teste no seu próprio notebook.
- **Segurança térmica:** proteção a 90 °C, vigia, e volta ao automático ao fechar, suspender ou travar.
- **Saúde:** histórico de telas azuis com o nome do erro, saúde da bateria e do disco.
- **Manutenção:** programas que iniciam com o Windows (com desfazer), limpeza de temporários, caches
  e logs (nunca toca em documentos, despejos de tela azul nem logs do sistema), reparo do Windows.
- **Testes embutidos:** `--autoteste` (confere cada função sem mudar nada) e `--teste-desempenho`
  (mede se cada modo rende mais ou menos que o Dia a dia).

Sem driver, sem serviço, sem nada do fabricante: só chamadas ao firmware pela interface WMI
`root\WMI AcerGamingFunction` e APIs do Windows.

## Compatibilidade

Validado num Acer Nitro V15. Outros modelos da família usam a mesma interface de firmware, mas com
diferenças (veja `docs/pesquisa-modelos-acer.md`). Em modelos não confirmados, o controle de
ventoinha e de modos fica travado até passar no teste guiado no próprio aparelho.

## Compilar

Precisa só do Windows 10/11 (o compilador C# do .NET Framework 4.8 já vem nele). Sem SDK, sem NuGet.

```
build.cmd          compila tudo (app, testes e sonda)
bin\ForjaTests.exe roda os testes
```

## Instalar

1. Baixe **SenseMonitor.exe** na página [Releases](https://github.com/carlosbrito-afk/SenseMonitor/releases/latest).
2. Crie uma **pasta vazia** (por exemplo `C:\Temp\SenseMonitor`), mova o `SenseMonitor.exe` para lá e
   abra de lá (não da pasta Downloads, que costuma ter outros arquivos). Abre o **instalador**, uma janela
   só, em oito passos:
   1. **Bem-vindo:** o que o app é, e o botão **Instalar**. É aqui que o Windows pede permissão de
      administrador (o firmware só responde a processos elevados); a janela continua do passo 2 na cópia
      com permissão. "Abrir sem instalar" usa o app só daquela vez, sem copiar nada.
   2. **Notebook:** reconhece o seu notebook (só leitura) e já mostra o que o firmware anuncia. Se não for
      um Acer com a interface gamer, o instalador explica que o app vai funcionar só como monitor, saúde e
      manutenção, e segue.
   3. **Programas:** serviços da Acer que disputam as ventoinhas; você decide se desativa (dá para desfazer).
   4. **Instalando:** copia para `%ProgramFiles%\SenseMonitor`, cria as tarefas agendadas (início com o
      Windows, guardião das ventoinhas, atualizador), os atalhos do Menu Iniciar (**SenseMonitor** e
      **Desinstalar SenseMonitor**) e a entrada em Apps e recursos. Cada passo aparece na tela; se um passo
      essencial falhar, o que já tinha sido feito é **desfeito** e a janela explica o que houve.
   5. **Tecla, Ventoinhas, Modos:** os testes guiados deste notebook, agora com o guardião das ventoinhas
      já instalado. Cada um tem **Fazer depois**: o que ficar sem teste continua bloqueado e o app avisa na
      primeira vez que abrir.
   6. **Pronto:** o resumo do que foi liberado e **Abrir o SenseMonitor** (abre o app instalado pela tarefa
      agendada e guarda o resultado dos testes na configuração dele).

   **Cancelar** antes do passo 4 terminar não deixa nada instalado (um passo em andamento é desfeito e os
   serviços da Acer que o teste desativou voltam como estavam). Cancelar depois: o app fica instalado, os
   testes que não foram concluídos continuam bloqueados e o app chama o teste de ambiente na primeira vez
   que abrir.

   Por segurança, **qualquer** abertura com permissão de administrador de um `SenseMonitor.exe` que não
   seja o instalado (instalar, reinstalar, ferramentas como `--autoteste`, `--desinstalar`) **recusa** se
   houver ao lado do exe algum arquivo `.config`, `.dll`, `.manifest` ou `.local` (o Windows os carregaria
   junto, com permissão de administrador); a verificação é a mesma em todos os casos e acontece antes de
   qualquer pedido de permissão (e antes da janela do instalador). A única coisa que a cópia com permissão
   recebe da janela inicial é o nome do passo em que continua (`--instalar --passo=reconhecer`); nenhum
   caminho, resultado ou arquivo de estado atravessa essa fronteira. Se o SenseMonitor **já estiver
   instalado**, abrir um exe baixado de outro lugar não eleva esse arquivo: o app pergunta se você quer
   abrir o que está instalado (padrão) ou atualizar/reinstalar com este arquivo (aí a verificação roda; a
   reinstalação usa os mesmos passos de cópia, atalhos e desfazer, sem a janela de testes).
3. Pronto: ele passa a abrir sozinho quando você entra no Windows (sem pedir confirmação) e aparece na
   bandeja do relógio. Abrir o SenseMonitor pelo Menu Iniciar mostra o que já está aberto ou o inicia
   pela tarefa agendada, sem repetir a pergunta do Windows.

O executável ainda não tem assinatura de editor do Windows (Authenticode), então o SmartScreen pode
mostrar "O Windows protegeu o computador": clique em *Mais informações* e *Executar assim mesmo*. Para
conferir o arquivo antes, compare o SHA-256 dele com o do `manifest.json` da mesma release.

Os testes da tecla, das ventoinhas e dos modos já fazem parte do instalador. Para refazê-los depois (outra
BIOS, outro Windows): **Configurações > Refazer teste de ambiente**, ou
`"%ProgramFiles%\SenseMonitor\SenseMonitor.exe" --teste-ambiente` com o app fechado.

## Atualizações

O app confere uma vez por dia (e 2 minutos depois de abrir) se há versão nova nesta página de Releases
e se atualiza sozinho, em silêncio, quando está ocioso: janela fechada, nenhuma ventoinha em
Manual/Curva/Máximo, nenhuma manutenção ou teste rodando. Antes de trocar o executável as ventoinhas
voltam ao automático (o mesmo caminho do "Sair").

Como o app roda como administrador, uma atualização falsa seria grave; por isso:

- cada release traz um `manifest.json` assinado (RSA 3072 + SHA-256, chave pública embutida no app);
- o app só aceita se a assinatura confere, a versão é **maior** que a instalada (nunca volta) e o tamanho
  e o SHA-256 do executável baixado batem com o manifesto assinado;
- baixa só de `github.com` (e dos servidores de download dele), por HTTPS, sem enviar dado pessoal nem
  identificador da máquina;
- guarda o arquivo numa pasta que só administradores escrevem e confere tudo de novo antes de aplicar;
  o estado, as confirmações e o histórico da atualização ficam em `%ProgramData%\SenseMonitor\atualizacao`
  (só administradores escrevem; o app confere o dono e as permissões da pasta antes de cada uso e não
  usa a pasta do seu perfil para isso);
- quem troca o executável é um aplicador que é uma cópia exata da versão que está rodando (já conhecida e
  testada), nunca o código da versão nova; o app só fecha depois que o aplicador confirma que tudo confere;
- se a versão nova não provar que funciona nos primeiros minutos, o aplicador volta para a anterior e
  não tenta essa versão de novo; em qualquer falha ele reabre o app.

Dá para desligar em **Configurações > Atualizações** ("Atualizar automaticamente"); o aviso na bandeja
depois de atualizar também. O histórico fica em `%ProgramData%\SenseMonitor\atualizacao\logs\atualizacao.log`
(qualquer conta pode ler só esse arquivo; o resto da pasta, com o estado e as confirmações, é só de
administradores). Se um usuário comum já tiver criado essa pasta antes (ou ela não tiver o dono e as
permissões certos), o app **não mexe nela nem apaga nada**: renomeia a pasta para `...recusado-<número>`, cria uma
pasta nova já com as permissões certas, confere por identificador (dono, permissões, não ser link) e só então a usa;
se não conseguir renomear, a atualização fica desligada (e registra isso).

## Remover

Configurações do Windows > Aplicativos > **SenseMonitor** > Desinstalar, o atalho **Desinstalar SenseMonitor**
do Menu Iniciar, o botão **Desinstalar…** em Configurações do próprio app, ou:

```
"%ProgramFiles%\SenseMonitor\SenseMonitor.exe" --desinstalar [--manter-dados]
```

A janela da desinstalação lista o que vai sair e pergunta **"Manter suas configurações?"** (Sim guarda o
`config.json`, o diário e o histórico de temperaturas para quando você instalar de novo; Não apaga tudo).
Com `--manter-dados` a pergunta já vem respondida (Sim) e a janela só pede a confirmação.

Em ordem: o app fecha sem ser forçado, as ventoinhas voltam ao automático, os serviços da Acer que o teste de
ambiente desativou voltam como estavam, as três tarefas agendadas (início, guardião das ventoinhas e
atualização), a entrada de Apps e recursos, os atalhos do Menu Iniciar, os dados da atualização e a pasta de
instalação saem, e (se você respondeu Não) os arquivos que o app grava em `%APPDATA%\SenseMonitor` também.
Esses são apagados **um a um, pelos nomes conhecidos, sem entrar em subpastas nem seguir links**; qualquer
outro arquivo seu que esteja lá fica, e a pasta só some se ficar vazia. Se o `config.json` estiver ilegível,
ele não é sobrescrito nem usado para restaurar os serviços da Acer (o aviso aparece no fim).

## Para desenvolvedores

`deploy.ps1` compila, testa e instala a sua build local em `%ProgramFiles%\SenseMonitor` (PowerShell
como administrador, na pasta do projeto):

```
powershell -ExecutionPolicy Bypass -File .\deploy.ps1
```

Uma build local não se atualiza sozinha enquanto a chave de assinatura embutida for a de exemplo
(`src\Core\UpdateKey.cs`). As releases saem de `publish\lancar.ps1` (veja o próprio script) e da
ferramenta `bin\ForjaRelease.exe`, que não faz parte do app.

**Chave de assinatura.** `bin\ForjaRelease.exe gerar-chave` cria a chave privada **criptografada com uma
frase-senha** (PBKDF2-SHA256 com 600 mil iterações, AES-256-CBC e HMAC-SHA256) em
`%USERPROFILE%\.sensemonitor-release\chave-privada.enc`, legível só pelo seu usuário. A frase-senha é
digitada no console (sem aparecer na tela) ao gerar a chave e **a cada assinatura**; ela não é guardada em
lugar nenhum e não existe forma de passá-la por argumento, variável de ambiente ou arquivo. Guarde a
frase-senha num gerenciador de senhas e faça backup do arquivo. **Perder a frase-senha ou o arquivo
significa perder a chave:** ninguém mais recebe atualizações assinadas e quem já instalou terá de
reinstalar à mão (com uma build que tenha a chave pública nova). Por isso o `lancar.ps1` precisa de um
console interativo.

## Autor

Criado por **carlosbrito-afk** (ideia, direção do produto, testes no hardware), com código escrito
com a ajuda do Claude (Anthropic).

## Licença

Domínio público — veja `LICENSE` (The Unlicense). Use, copie, modifique e distribua como quiser.
