# Oracle VPS relay for AeroFoil

Friends connect to a small cloud server that holds no games. It checks every request and passes the
allowed ones down an encrypted WireGuard link to the AeroFoil server in your home. Your games stay
at home.

```
Friend's app ──HTTPS──▶ aerofoil.yourdomain.com  (Oracle VPS: nginx allowlist, Let's Encrypt, fail2ban)
                                 │
                                 └──WireGuard (10.13.13.1 ⇄ 10.13.13.2)──▶ home: socat ──▶ aerofoil:8465
```

What this gives you:

- **Your home IP stays hidden.** Friends only ever see the VPS's address.
- **No port forward at home.** Your home server dials out to the VPS and keeps the link open.
- **One WireGuard link in total.** There's nothing to set up per friend and nothing to change on
  your router. Friends keep using their AeroFoil logins.
- **Only the app's endpoints are reachable**, the same allowlist as before. The tunnel itself can
  only reach AeroFoil's port 8465: nothing else on your LAN.
- **No Cloudflare proxy or tunnel**, so Cloudflare's large-file terms and the tunnel's speed issues
  no longer apply.

Speed is still capped by your home upload (you measured about 5 MB/s).

Files in this folder:

| File | Where it goes |
|---|---|
| `vps/wg0.conf` | VPS: `/etc/wireguard/wg0.conf` |
| `vps/docker-compose.yml` | VPS: e.g. `~/relay/docker-compose.yml` |
| `vps/aerofoil.subdomain.conf` | VPS: `~/relay/swag/nginx/proxy-confs/aerofoil.subdomain.conf` |
| `home/docker-compose.yml` | Home Docker host: e.g. `~/aerofoil-relay/docker-compose.yml` |
| `home/wg0.conf` | Home Docker host: `~/aerofoil-relay/wireguard/wg_confs/wg0.conf` |

Placeholders to replace: `<VPS_PRIVATE_KEY>`, `<VPS_PUBLIC_KEY>`, `<HOME_PRIVATE_KEY>`,
`<HOME_PUBLIC_KEY>`, `<VPS_PUBLIC_IP>`, `yourdomain.com`, `you@example.com` and `aerofoil_net`.

---

## 1. Create the Oracle VPS

1. In Oracle Cloud, go to **Compute → Instances → Create instance**.
   - **Image:** Canonical Ubuntu 24.04.
   - **Shape:** Ampere A1 (`VM.Standard.A1.Flex`, 1 OCPU / 6 GB is plenty). The AMD
     `VM.Standard.E2.1.Micro` also works for a relay.
   - Add your SSH public key.
2. Give it a **reserved public IP**, so the address never changes: **Networking → IP Management →
   Reserved public IPs**, then attach it to the instance's VNIC. Your DNS record depends on this
   address staying the same.
3. Open the ports in Oracle's cloud firewall: **Networking → Virtual cloud networks → your VCN →
   Security Lists → Default → Add Ingress Rules** (source `0.0.0.0/0`):
   - TCP `80` (Let's Encrypt certificate checks)
   - TCP `443` (HTTPS)
   - UDP `51820` (WireGuard)
4. *Recommended:* upgrade the account to **Pay As You Go**. Oracle reclaims "idle" Always Free
   instances in free-tier accounts, and a relay that's quiet most of the time can look idle.
   Pay As You Go accounts still get the Always Free resources at no cost.

## 2. Open the ports on the VPS itself

Oracle's Ubuntu images ship with an `iptables` firewall that rejects everything except SSH, in
addition to the Security List. SSH in and run:

```bash
sudo iptables -I INPUT -p tcp --dport 80 -j ACCEPT
```

```bash
sudo iptables -I INPUT -p tcp --dport 443 -j ACCEPT
```

```bash
sudo iptables -I INPUT -p udp --dport 51820 -j ACCEPT
```

```bash
sudo netfilter-persistent save
```

## 3. WireGuard keys and the VPS side of the link

Install WireGuard and Docker on the VPS:

```bash
sudo apt update && sudo apt install -y wireguard docker.io docker-compose-v2
```

Generate a key pair for the VPS and another for your home server. The home keys are created here
for convenience; copy the home private key across securely (e.g. `scp`) and then delete it from
the VPS.

```bash
umask 077; wg genkey | tee vps.key | wg pubkey > vps.pub; wg genkey | tee home.key | wg pubkey > home.pub
```

```bash
head vps.key vps.pub home.key home.pub
```

Copy `vps/wg0.conf` to `/etc/wireguard/wg0.conf`, then fill in `<VPS_PRIVATE_KEY>` (contents of
`vps.key`) and `<HOME_PUBLIC_KEY>` (contents of `home.pub`). Then start the tunnel and enable it at
boot:

```bash
sudo chmod 600 /etc/wireguard/wg0.conf && sudo systemctl enable --now wg-quick@wg0
```

## 4. The home side of the link

On the Docker host where AeroFoil runs:

1. Find the Docker network AeroFoil is on. It's the one your current SWAG uses to reach
   `aerofoil`:

   ```bash
   docker inspect aerofoil --format "{{range $k, $v := .NetworkSettings.Networks}}{{$k}} {{end}}"
   ```

   Put that name in `home/docker-compose.yml` in place of `aerofoil_net`.
2. Copy `home/wg0.conf` to `./wireguard/wg_confs/wg0.conf` next to the compose file. Fill in
   `<HOME_PRIVATE_KEY>` (from `home.key`), `<VPS_PUBLIC_KEY>` (from `vps.pub`) and
   `<VPS_PUBLIC_IP>`.
3. Start it:

   ```bash
   docker compose up -d
   ```

4. Check the link. Run this on the VPS; "latest handshake" should be only a few seconds old:

   ```bash
   sudo wg show
   ```

5. Check that the VPS can reach AeroFoil through the tunnel. On the VPS, with your AeroFoil
   username:

   ```bash
   curl -u youruser -A "EdenGameLibraryManager/test" http://10.13.13.2:8465/api/shop/sections
   ```

   You should get JSON starting with `{"sections"` (or `{"rotation_key"`).

## 5. DNS in Cloudflare

1. Delete the old tunnel route for this name: **Zero Trust → Networks → Tunnels → your tunnel →
   Public Hostname**, then remove `aerofoil.yourdomain.com`. Also delete its `CNAME` record under
   **DNS → Records** if one is left.
2. Add an **A** record: name `aerofoil`, IPv4 = the VPS's reserved public IP, **Proxy status: DNS
   only** (grey cloud).

The Cloudflare WAF and cache rules from before no longer apply to this name, because traffic no
longer passes through Cloudflare. The VPS's nginx does the same job.

## 6. nginx and the certificate on the VPS

```bash
mkdir -p ~/relay && cd ~/relay
```

Copy `vps/docker-compose.yml` here and set `URL`, `SUBDOMAINS` and `EMAIL`. Start it once so SWAG
gets the certificate and creates its folders:

```bash
docker compose up -d && docker logs -f swag
```

Wait for `Server ready`, then press Ctrl+C. Copy `vps/aerofoil.subdomain.conf` to
`~/relay/swag/nginx/proxy-confs/aerofoil.subdomain.conf`, then check and reload:

```bash
docker exec swag nginx -t
```

```bash
docker exec swag nginx -s reload
```

*Prefer not to open port 80?* Use DNS validation instead: set `VALIDATION=dns` and add
`DNSPLUGIN=cloudflare` in the compose file. Then put a Cloudflare API token with **Zone → DNS →
Edit** permission into `~/relay/swag/dns-conf/cloudflare.ini` and remove the port-80 rules.

## 7. AeroFoil: trust the relay's forwarded client IP

AeroFoil now sees every connection coming from the `wireguard-relay` container. Tell it to trust
the `X-Forwarded-For` header that the VPS sets, so its failed-login lockout applies per person
instead of to everyone. AeroFoil's settings file is YAML: `settings.yaml` in the folder mounted
at `/app/config` (`./config/settings.yaml` with AeroFoil's own compose file). Find the existing
`security:` section and set these two keys inside it. Don't add a second `security:` block.

```yaml
security:
  trust_proxy_headers: true
  trusted_proxies:
    - "172.18.0.0/16"
```

Use your AeroFoil Docker network's subnet. Find it with:

```bash
docker network inspect aerofoil_net --format "{{range .IPAM.Config}}{{.Subnet}}{{end}}"
```

Restart AeroFoil afterwards.

## 8. Test

- **In a browser:** `https://aerofoil.yourdomain.com` should show **403 Forbidden**. That's the
  allowlist working.
- **In the app:** the server URL stays `https://aerofoil.yourdomain.com`. Click **Refresh** (or
  **Test Connection**) and it should connect. Downloads should work, including resuming.
- **Logs if something is refused:**

  ```bash
  docker exec swag tail -f /config/log/nginx/access.log
  ```

Once this works, you can remove the old `cloudflared` container and any router port forward to
your home nginx. Neither is needed any more.

## Troubleshooting

| Symptom | Check |
|---|---|
| No handshake in `sudo wg show` | UDP 51820 is open in both the Security List **and** the VPS `iptables` rules; keys and VPS IP are correct in the home `wg0.conf`; `docker logs wireguard-relay` on the home host |
| `curl http://10.13.13.2:8465` from the VPS times out | `docker logs aerofoil-forward`; the `aerofoil_net` name is right; AeroFoil's container is named `aerofoil` |
| Certificate errors | Port 80 is open (or use DNS validation); the A record points at the VPS and is **DNS only** |
| The app says "access denied" | The nginx access log shows which path or user agent got a 403 |
| Everyone gets locked out after one person mistypes a password | Step 7: `trusted_proxies` / `trust_proxy_headers` |
