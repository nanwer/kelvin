# Releasing Kelvin

## How updates work

Kelvin checks `https://github.com/nanwer/kelvin/releases/latest/download/latest.json` a few times a
day. That file names the newest version and the installer's download link, with a signature.
The app only installs an update whose signature matches the **public key built into it**
(`plugins.updater.pubkey` in `desktop/tauri.conf.json`). Anything else is refused, even if it
comes from the right GitHub repo.

The matching **private key** signs every release. Keep it safe:

- It lives in `%USERPROFILE%\.kelvin-signing\` (`updater.key` and `password.txt`) on the
  maintainer's PC, and as the GitHub secrets below. It is never committed.
- **Back it up** (for example in a password manager). If it's lost, existing installs can never
  update again: everyone would have to reinstall by hand with a new key.
- If it leaks, anyone could ship "updates". Generate a new key, publish a release with the new
  public key, and treat the old one as compromised.

## One-time setup

### 1. Updater key secrets (required)

```bash
gh secret set TAURI_SIGNING_PRIVATE_KEY < ~/.kelvin-signing/updater.key
gh secret set TAURI_SIGNING_PRIVATE_KEY_PASSWORD < ~/.kelvin-signing/password.txt
```

### 2. Code signing with SignPath (removes the SmartScreen warning)

[SignPath Foundation](https://signpath.org) signs open-source projects for free.

1. Apply at https://signpath.org/apply. Kelvin qualifies: MIT licensed, public, built by GitHub
   Actions, released through GitHub Releases.
2. Once approved, in SignPath:
   - Create project **`kelvin`**, linked to this GitHub repository as a trusted build system.
   - Create artifact configuration **`installer`**:
     ```xml
     <artifact-configuration xmlns="http://signpath.io/artifact-configuration/v1">
       <zip-file>
         <pe-file path="*.exe">
           <authenticode-sign />
         </pe-file>
       </zip-file>
     </artifact-configuration>
     ```
   - Create signing policy **`release-signing`**.
3. In GitHub, add:
   - secret `SIGNPATH_API_TOKEN` (a SignPath CI user's API token)
   - variable `SIGNPATH_ORGANIZATION_ID`

Until these exist, the workflow skips code signing and everything else still works.

## Cutting a release

1. Bump the version in **all three** places:
   `desktop/tauri.conf.json`, `desktop/Cargo.toml`, `src/Kelvin.Service/Kelvin.Service.csproj`.
2. Commit, then tag and push:
   ```bash
   git tag v0.2.1
   git push origin v0.2.1
   ```
3. The **Release** workflow tests, builds, code-signs, signs the update and creates a **draft**
   release with the installer, its `.sig`, and `latest.json`.
4. Check the draft (download the installer and try it), edit the notes, then click **Publish**.
   Installed copies see the update within a few hours, or right away with *Settings → Check now*.

## Building a release locally

`./build-installer.ps1` picks up the key from `%USERPROFILE%\.kelvin-signing\` and writes the
installer, `.sig` and `latest.json`. Upload all three to a release. Without the key it builds a
normal installer without update files.
