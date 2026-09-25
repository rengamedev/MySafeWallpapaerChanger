# Wallpaper Rotator

Утилита для Windows 10/11 (.NET 8, WinForms, `net8.0-windows`), описание и сборка — в
`README.md`. Приложение не должно зависеть от NuGet-пакетов; xUnit есть только у тестов.

## Git и CI

Схема одна на все репозитории rengamedev.

- Работа только через ветку и PR: прямой push в `master` отклоняется ruleset'ом,
  слияние возможно только кнопкой Squash and merge (один PR = один коммит).
- Перед созданием PR прогони тесты локально (`dotnet test WallpaperRotator.sln -c Release`
  или `build.cmd`) и проверь форматирование
  (`dotnet format whitespace --folder --verify-no-changes`). Если всё прошло, создай PR:
  когда workflow `tests` на нём закончится зелёным, `auto-merge.yml` сам сольёт PR
  squash-ом и удалит ветку. Если тесты падают, PR не создавай, сначала почини.
- `auto-merge.yml` срабатывает на завершение `tests` (`workflow_run`) и сливает
  только открытый не-draft PR владельца в `master`, чья голова — проверенный
  коммит, а все обязательные проверки в `pass`; придержать PR — держать его в
  draft. Штатный авто-мерж на открытии PR не возвращать: GitHub не включает его
  на PR, который уже можно слить, и docs-only `tests` обгонял вызов.
  Слияние от `GITHUB_TOKEN` не запускает других workflow и не удаляет ветку
  настройкой репозитория, поэтому ветку удаляет `--delete-branch`.
  `workflow_run` берёт файл из `master`: правка `auto-merge.yml` на своём PR не
  запускается и действует только после слияния. Появится обязательная проверка
  из другого workflow — впиши его в `workflows:`.
- После слияния локальный `master` только обновлять: `git checkout master &&
  git pull`. Не сливать в него ветки локально (`git merge`, `git pull` из
  ветки) и не коммитить в него напрямую: PR попадает в `master` squash-коммитом
  с другим хешем, локальный merge создаёт расходящуюся историю, и следующий
  `git pull` заканчивается конфликтом. Если локальный `master` всё же разошёлся
  с `origin/master`, а его содержимое уже на GitHub, выровнять:
  `git fetch origin && git reset --hard origin/master`.
- `.github/workflows/tests.yml`: job `tests` на Ubuntu на каждом готовом (не draft)
  PR с изменениями кода собирает решение (`EnableWindowsTargeting`), проверяет
  форматирование и отсутствие NuGet-пакетов у приложения; обязательна в ruleset.
  Тесты на Ubuntu не запускаются (WinForms), поэтому их прогоняй локально до PR.
  Job `windows` гоняет `dotnet test` на Windows раз в сутки по расписанию и по кнопке
  Run workflow. Каждый `uses:` закреплён полным SHA коммита.
- `.github/workflows/build.yml` собирает EXE и публикует GitHub Release только по тегу
  `v*` (и по кнопке Run workflow).
