---
title: "Split Control/Data Plane deployment"
description: "Share PostgreSQL, keys, and workspaces, and route requests by Host role."
weight: 20
lastmod: 2026-09-15
translationKey: docs/operations/split
---

Split deployment runs management and scheduling in Control Plane and task execution in Data Plane. Use it when execution environments need separate maintenance or additional nodes. For a trial on one host, [Standalone]({{< relref "/docs/operations/standalone" >}}) is simpler to configure.

This page assumes familiarity with containers, databases, and reverse proxies. Prepare shared PostgreSQL, Data Protection keys for decrypting credentials, and workspaces accessible to every execution node. The entry proxy must support WebSocket for conversation events.

## Roles

| Host | Responsibility |
| --- | --- |
| Control Plane | Setup, Web, management APIs, Job scheduling |
| Data Plane | SignalR Execution, A2A, durable execution workers |
| Standalone | Both roles combined for a single-server setup |

Split deployments require PostgreSQL for the database and locks, plus Distributed execution on both roles. SQLite or in-memory locks cannot replace cross-node coordination.

```yaml
Database__Provider: postgres
Database__ConnectionString: "${AGW_DATABASE_CONNECTION_STRING}"
Execution__Provider: Distributed
DistributedLock__Provider: postgres
DistributedLock__ConnectionString: ""
```

This is an environment configuration fragment for both roles. An empty lock connection string reuses the database connection. Supply the real database connection string through Secrets.

## Startup and routing

1. Configure the database, both Hosts, shared keys, and directories using the cluster Compose reference.
2. Start Control Plane first, initialize it, and confirm readiness.
3. Start Data Plane, then add replicas as needed.
4. Route `/api/hubs/exec`, `/a2a/*`, and `/.well-known/agents.json` to Data Plane; route other application paths to Control Plane.

Preserve Host, authentication headers/Cookies, and WebSocket Upgrade. Exclude execution Hub query strings from proxy access logs. Control Plane does not serve A2A.

## Reading the examples

For Docker Compose, start with the cluster Compose file linked below and follow the startup order and routes above. For Kubernetes, the next section uses kind, which runs a local Kubernetes cluster in containers. Pods run applications, Services provide access addresses, and PVs/PVCs declare and request storage.

Both approaches need a shared client entry point. The Nginx section shows which requests go to each role. Verify the services and database first, then routing, to distinguish service failures from proxy failures.

## Kubernetes YAML examples

The repository’s [deploy/k8s](https://github.com/zxyao145/agw/tree/main/deploy/k8s) directory provides a **local, single-node kind** example. It uses separate Control Plane and Data Plane Deployments, an external PostgreSQL database, and NodePorts that can connect to the Nginx configuration above. These files do not create PostgreSQL or an Ingress Controller.

| File | Purpose |
| --- | --- |
| [kind-agw-cluster.yaml](https://github.com/zxyao145/agw/blob/main/deploy/k8s/kind-agw-cluster.yaml) | Create the local kind cluster with port and directory mappings |
| [agw-data-pv-pvc.yaml](https://github.com/zxyao145/agw/blob/main/deploy/k8s/agw-data-pv-pvc.yaml) | Provide a data volume shared by Pods on the same node |
| [agw-control-plane-deployment.yaml](https://github.com/zxyao145/agw/blob/main/deploy/k8s/agw-control-plane-deployment.yaml) | One Control Plane replica and a NodePort Service |
| [agw-data-plane-deployment.yaml](https://github.com/zxyao145/agw/blob/main/deploy/k8s/agw-data-plane-deployment.yaml) | Two Data Plane replicas and a NodePort Service |

### Cluster entry points and shared directory

`kind-agw-cluster.yaml` maps both NodePorts to the host loopback address and mounts `/opt/agw` into the kind node:

```yaml
kind: Cluster
apiVersion: kind.x-k8s.io/v1alpha4
nodes:
  - role: control-plane
    extraPortMappings:
      - containerPort: 30816
        hostPort: 30816
        listenAddress: "127.0.0.1"
        protocol: TCP
      - containerPort: 30820
        hostPort: 30820
        listenAddress: "127.0.0.1"
        protocol: TCP
    extraMounts:
      # Required by agw-data-pv: expose the host directory inside the kind node.
      - hostPath: /opt/agw
        containerPath: /opt/agw
```

Prepare `/opt/agw/agw-data` on the container runtime host before creating the cluster. With a Docker/Podman VM, configure file sharing so the directory is available inside the VM. The `control-plane` node role here belongs to Kubernetes; it is distinct from AGW’s Control Plane service.

The storage path is:

```text
Host /opt/agw/agw-data
  → kind node /opt/agw/agw-data
  → PV agw-data-pv → PVC agw-data
  → /data in Control/Data Plane Pods
```

The corresponding PV/PVC follows. `Retain` preserves reclaimed volume data but does not replace backups. The PVC requests `1Gi`, while the PV declares `5Gi` capacity.

```yaml
apiVersion: v1
kind: PersistentVolume
metadata:
  name: agw-data-pv
  labels:
    app: agw
spec:
  capacity:
    storage: 5Gi
  accessModes:
    - ReadWriteOnce
  persistentVolumeReclaimPolicy: Retain
  storageClassName: manual
  hostPath:
    # Node path backed by kind-agw-cluster.yaml extraMounts; single-node use only.
    path: /opt/agw/agw-data
    type: DirectoryOrCreate
---
apiVersion: v1
kind: PersistentVolumeClaim
metadata:
  name: agw-data
spec:
  accessModes:
    - ReadWriteOnce
  storageClassName: manual
  resources:
    requests:
      storage: 1Gi
  volumeName: agw-data-pv
```

`ReadWriteOnce` permits multiple Pods on the same node to mount the volume, covering both roles and Data Plane replicas in this example. **hostPath is not shared storage across nodes.** For multiple nodes, use shared storage supported by your cluster and keep keys, credentials, and Project workspace paths consistent across execution nodes. Add mounts for workspaces located outside `/data`.

### Data Plane Deployment and Service

This is the repository’s complete Data Plane example. It runs two replicas on container port `8080`, reads the `agw-database` Secret, and exposes Service port `30820`. The Control Plane file uses the same volume and database settings, with one replica and NodePort `30816`. It also reads `password` from `agw-admin` as `Setup__AdminPassword`.

```yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: agw-data-plane
  labels:
    app: agw-data-plane
spec:
  replicas: 2
  selector:
    matchLabels:
      app: agw-data-plane
  template:
    metadata:
      labels:
        app: agw-data-plane
    spec:
      containers:
        - name: agw-data-plane
          image: localhost/agw-data-plane:local
          imagePullPolicy: Never
          env:
            - name: AgwLogDir
              value: /data/logs
            - name: AgwDataDir
              value: /data
            - name: ASPNETCORE_ENVIRONMENT
              value: Production
            - name: ASPNETCORE_URLS
              value: http://0.0.0.0:8080
            - name: DistributedLock__Provider
              value: postgres
            - name: Execution__Provider
              value: Distributed
            - name: Database__Provider
              value: postgres
            - name: Database__ConnectionString
              valueFrom:
                secretKeyRef:
                  name: agw-database
                  key: connection-string
          ports:
            - name: http
              containerPort: 8080
              protocol: TCP
          securityContext:
            # The local hostPath is owned by the host user with mode 0700; preserve the
            # current local Podman setup's root access so the server can write /data.
            runAsUser: 0
            runAsGroup: 0
            runAsNonRoot: false
          volumeMounts:
            - name: agw-data
              mountPath: /data
      volumes:
        - name: agw-data
          persistentVolumeClaim:
            claimName: agw-data
---
apiVersion: v1
kind: Service
metadata:
  name: agw-data-plane
  labels:
    app: agw-data-plane
spec:
  type: NodePort
  sessionAffinity: ClientIP
  sessionAffinityConfig:
    clientIP:
      timeoutSeconds: 10800
  selector:
    app: agw-data-plane
  ports:
    - name: http
      protocol: TCP
      port: 30820
      targetPort: http
      nodePort: 30820
```

Before using it, check:

- **Images**: `localhost/agw-…:local` with `imagePullPolicy: Never` requires images to be built and loaded into kind first. For registry images, use reachable image addresses and versions, with an appropriate pull policy and credentials.
- **Database**: both roles must use the same PostgreSQL database reachable from the Pods. `localhost` in a connection string refers to the Pod itself, usually not the host database.
- **Permissions**: the local example runs as root to accommodate its directory permissions. Set an appropriate UID/GID for your storage in other environments instead of copying this local setting unchanged.
- **Connection affinity**: the Service uses `ClientIP` affinity to help SignalR requests reach the same Pod. With Nginx outside the cluster, multiple clients may appear as one proxy IP, so this does not guarantee even load distribution.

### Deployment order

Prepare the local images, data directory, and files containing the two Secret values, then run from the repository root. Secret files should contain only the relevant values; keep real credentials out of Git. These commands use the default namespace of the current kubectl context. If you choose another namespace, keep Deployments, Services, PVCs, and Secrets together.

```bash
# Run from the repository root, after preparing /opt/agw/agw-data.
kind create cluster --name agw --config deploy/k8s/kind-agw-cluster.yaml
kubectl apply -f deploy/k8s/agw-data-pv-pvc.yaml

# These images must already exist in the local container runtime.
kind load docker-image --name agw \
  localhost/agw-control-plane:local localhost/agw-data-plane:local

# Replace these paths with files containing the actual secret values.
kubectl create secret generic agw-database \
  --from-file=connection-string=/secure/agw-database-connection-string
kubectl create secret generic agw-admin \
  --from-file=password=/secure/agw-admin-password

kubectl apply -f deploy/k8s/agw-control-plane-deployment.yaml
kubectl rollout status deployment/agw-control-plane
kubectl logs deployment/agw-control-plane --tail=100

# Continue only after Control Plane initialization has completed.
kubectl apply -f deploy/k8s/agw-data-plane-deployment.yaml
kubectl rollout status deployment/agw-data-plane
kubectl get pods,services,pvc
```

`rollout status` confirms the Deployment rollout, not application initialization. In this example, Control Plane’s `Setup__AdminPassword` triggers first-run initialization. Check logs and the sign-in page before starting Data Plane. Existing database authentication settings are not overwritten by this initial password.

With the kind mappings above, the earlier Nginx upstreams can use `127.0.0.1:30816` and `127.0.0.1:30820`. Nginx inside the cluster can instead use `agw-control-plane:30816` and `agw-data-plane:30820` in the same namespace. After checking that the PVC is Bound and Pods are running, verify login, execution connections, and which nodes receive requests.

Do not run `kubectl apply -f deploy/k8s/`: the kind Cluster file is input to `kind`, not a Kubernetes API resource. Changing kind port or mount mappings requires recreating the cluster; back up data first. See the [local kind deployment guide](https://github.com/zxyao145/agw/blob/main/deploy/k8s/README.md) for the complete source instructions.

## Nginx configuration example

This example follows the routing structure of the local `agw.conf`: Nginx provides one entry point, Control Plane listens on `30816`, and Data Plane on `30820`. Replace these example ports with the actual Host listeners. For separate hosts or containers, replace `127.0.0.1` with addresses reachable from Nginx.

### Web hosted by Control Plane

Save this as a site configuration included from the **`http {}`** block in `nginx.conf`. `map`, `log_format`, and `upstream` must not be nested inside `server {}`. Log paths are relative to the Nginx prefix; create the directories or use writable absolute paths.

```nginx
# Included inside nginx.conf's http {} block.
map $http_upgrade $agw_connection_upgrade {
    default upgrade;
    ''      close;
}

# Do not log query strings, which may contain an execution token.
log_format agw_route '$remote_addr [$time_local] '
                     '"$request_method $uri $server_protocol" $status '
                     'upstream=$upstream_addr upstream_status=$upstream_status';

upstream agw_control_plane {
    server 127.0.0.1:30816;
}

upstream agw_data_plane {
    ip_hash;
    server 127.0.0.1:30820;
    # server 127.0.0.1:30821;
}

server {
    listen 80;
    server_name agw.example.com;

    client_max_body_size 100m;
    access_log logs/agw_access.log agw_route;
    error_log  logs/agw_error.log warn;

    proxy_http_version 1.1;
    proxy_set_header Host $host;
    proxy_set_header X-Forwarded-Host $host;
    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    proxy_set_header X-Forwarded-Proto $scheme;
    proxy_set_header X-Real-IP $remote_addr;
    proxy_set_header Upgrade $http_upgrade;
    proxy_set_header Connection $agw_connection_upgrade;
    proxy_read_timeout 3600s;
    proxy_send_timeout 3600s;

    # Includes /api/hubs/exec/negotiate and the WebSocket endpoint.
    location ^~ /api/hubs/exec {
        proxy_buffering off;
        proxy_pass http://agw_data_plane;
    }

    location ^~ /a2a/ {
        proxy_buffering off;
        proxy_pass http://agw_data_plane;
    }

    location = /.well-known/agents.json {
        proxy_pass http://agw_data_plane;
    }

    # Setup, management APIs, OpenAPI, and the hosted Web client.
    location / {
        proxy_pass http://agw_control_plane;
    }
}
```

The example uses HTTP for local verification. For external access, configure `listen 443 ssl;`, `ssl_certificate`, and `ssl_certificate_key` in this server, using your domain and valid certificate, and redirect HTTP to HTTPS.

| Request | Destination | Purpose |
| --- | --- | --- |
| `/api/hubs/exec` and child paths | Data Plane | SignalR negotiation and execution connections |
| `/a2a/*` | Data Plane | A2A requests and streaming responses |
| `/.well-known/agents.json` | Data Plane | Agent discovery |
| Other paths | Control Plane | Setup, management APIs, OpenAPI, Web pages, and static assets |

The `proxy_pass` directives have no URI suffix, preserving the original path and query parameters. Authentication headers and Cookies are forwarded by default. Upgrade headers and HTTP/1.1 support WebSockets. Disabling response buffering on execution and A2A routes lets streaming output reach clients promptly. The `3600s` values are proxy read/write timeouts, not a guarantee of uninterrupted tasks of any duration.

With multiple Data Plane instances, `ip_hash` aims to keep requests from one IP on the same instance so SignalR negotiation and subsequent connections reach the same node. It does not replace shared database, execution-state, and recovery configuration. If another proxy sits in front of Nginx, configure trusted proxies and client-IP handling for your network rather than trusting arbitrary `X-Forwarded-For` headers.

The access log uses `$uri` without query parameters. Its upstream field helps identify the receiving node. `client_max_body_size` controls only Nginx’s request limit; it does not increase AGW’s attachment limits.

### A separate Web service

If Web runs separately on `3001`, as in the reference configuration, retain the Data Plane routes and common proxy settings above. Add the `agw_web` upstream and the management routes below, replacing the original `location /`. Port `3001` is the repository’s Web development port; use your Web service’s actual port in deployment.

```nginx
# Add inside http {}, alongside the other upstream blocks.
upstream agw_web {
    server 127.0.0.1:3001;
}

# Add these locations inside the existing server {}.
location /api/ {
    proxy_pass http://agw_control_plane;
}
location /openapi/ {
    proxy_pass http://agw_control_plane;
}
location = /setup {
    proxy_pass http://agw_control_plane;
}
location /setup/ {
    proxy_pass http://agw_control_plane;
}

# Replace the existing location /; do not add a second one.
location / {
    proxy_pass http://agw_web;
}
```

Both `/setup` and `/setup/` reach Control Plane, as do ordinary `/api/` requests. The longer `/api/hubs/exec` match still reaches Data Plane. Configure the separate Web service’s own backend address to point to Control Plane. Clients should use the shared Nginx entry point.

### Check and load the configuration

After saving your deployment configuration, validate it before reloading:

```bash
nginx -t
nginx -s reload
```

Run the reload in your own deployment environment. Verify login and page assets. In browser network tools, check for a successful WebSocket upgrade (`101`) on the execution connection and confirm its upstream is Data Plane in the access log. Management APIs should reach Control Plane. For login failures, check Cookies, forwarded scheme, and application proxy-trust settings. For execution connection failures, check Upgrade headers, routing, and the Data Plane port.

## Verify

Test login, a Chat run, and a Job in order, inspecting the actual execution node. All Hosts read initialization and authentication from the same database. Recovery also requires consistent directories, keys, and runtime credentials; running containers alone do not prove readiness.

## Implementation and references

- [Cluster Compose](https://github.com/zxyao145/agw/blob/main/deploy/compose.cluster.yaml)
- [Ingress example](https://github.com/zxyao145/agw/blob/main/deploy/nginx.split.conf.example)
- [Deployment](https://github.com/zxyao145/agw/blob/main/docs/4.Deployment.md)
