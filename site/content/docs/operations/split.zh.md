---
title: "Control/Data Plane 分离部署"
description: "共享 PostgreSQL、密钥和工作目录，按角色路由请求。"
weight: 20
lastmod: 2026-09-15
translationKey: docs/operations/split
---

分离部署把管理和调度放在 Control Plane（控制面），把任务执行放在 Data Plane（数据面）。需要单独维护执行环境或增加执行节点时，可以采用这种方式；只在一台主机试用时，[Standalone]({{< relref "/docs/operations/standalone" >}})更容易配置。

本页面向熟悉容器、数据库和反向代理的部署者。开始前，准备共享 PostgreSQL、用于解密凭据的 Data Protection 密钥，以及各执行节点都能访问的工作目录。入口代理还需要支持 WebSocket，以便持续传输对话事件。

## 角色

| Host | 职责 |
| --- | --- |
| Control Plane | Setup、Web、管理 API、Jobs 调度 |
| Data Plane | SignalR Execution、A2A、持久化执行 workers |
| Standalone | 合并两种职责，适合单机 |

分离部署要求两端使用 PostgreSQL 数据库、Distributed 执行和 PostgreSQL 锁。不能使用 SQLite 或内存锁替代跨节点协调。

```yaml
Database__Provider: postgres
Database__ConnectionString: "${AGW_DATABASE_CONNECTION_STRING}"
Execution__Provider: Distributed
DistributedLock__Provider: postgres
DistributedLock__ConnectionString: ""
```

这是注入两端的环境配置片段。连接字符串为空的锁配置复用数据库连接；实际数据库连接字符串通过 Secrets 提供。

## 启动与路由

1. 按仓库 cluster Compose 配置数据库、两个 Host、共享密钥和目录。
2. 先启动 Control Plane，完成初始化并确认就绪。
3. 再启动 Data Plane，最后按需要增加副本。
4. 将 `/api/hubs/exec`、`/a2a/*` 和 `/.well-known/agents.json` 路由到 Data Plane，其余应用路径到 Control Plane。

保留 Host、认证头/Cookie 和 WebSocket Upgrade；执行 Hub 查询字符串不应写入代理访问日志。Control Plane 不提供 A2A。

## 如何阅读下面的示例

Docker Compose 部署从文末的 cluster Compose 文件开始，并按上一节验证启动顺序和路由。Kubernetes 部署可参考下面的本地 kind 示例；其中 kind 是在容器中运行本地 Kubernetes 集群的工具，Pod 是运行应用的单元，Service 提供访问地址，PV/PVC 用于声明和申请存储。

两种部署都需要统一的客户端入口。Nginx 一节说明哪些请求发送到控制面、哪些发送到数据面。先确保两个服务和数据库可用，再检查入口转发，便于区分服务自身与代理的问题。

## Kubernetes YAML 示例

仓库的 [deploy/k8s](https://github.com/zxyao145/agw/tree/main/deploy/k8s) 提供一套**本地单节点 kind** 示例。它将 Control Plane 和 Data Plane 分成独立 Deployment，使用外部 PostgreSQL，并通过 NodePort 接入前面的 Nginx。以下内容对应这些文件，不会额外创建 PostgreSQL 或 Ingress Controller。

| 文件 | 用途 |
| --- | --- |
| [kind-agw-cluster.yaml](https://github.com/zxyao145/agw/blob/main/deploy/k8s/kind-agw-cluster.yaml) | 创建本地 kind 集群，映射端口与宿主目录 |
| [agw-data-pv-pvc.yaml](https://github.com/zxyao145/agw/blob/main/deploy/k8s/agw-data-pv-pvc.yaml) | 提供共享给同一节点上各 Pod 的数据卷 |
| [agw-control-plane-deployment.yaml](https://github.com/zxyao145/agw/blob/main/deploy/k8s/agw-control-plane-deployment.yaml) | 1 个 Control Plane 副本和 NodePort Service |
| [agw-data-plane-deployment.yaml](https://github.com/zxyao145/agw/blob/main/deploy/k8s/agw-data-plane-deployment.yaml) | 2 个 Data Plane 副本和 NodePort Service |

### 集群入口与共享目录

`kind-agw-cluster.yaml` 将两个 NodePort 映射到宿主机的回环地址，同时将 `/opt/agw` 挂入 kind 节点：

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

创建集群前，在容器运行时所在主机准备 `/opt/agw/agw-data`。使用 Docker/Podman 虚拟机时，还需通过文件共享配置使该路径在虚拟机中可用。`role: control-plane` 指 Kubernetes 节点角色，与 AGW 的 Control Plane 服务不是同一概念。

数据的实际路径为：

```text
宿主机 /opt/agw/agw-data
  → kind 节点 /opt/agw/agw-data
  → PV agw-data-pv → PVC agw-data
  → Control/Data Plane Pod 内 /data
```

下面是对应的 PV/PVC。`Retain` 保留回收后的卷数据，但不代替备份；PVC 的 `1Gi` 是请求容量，PV 声明的容量为 `5Gi`。

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

`ReadWriteOnce` 允许同一节点上的多个 Pod 挂载，因此本例两个角色及 Data Plane 副本可以共用该卷。**hostPath 不提供跨节点共享存储**；多节点部署需要替换成集群支持的共享存储，并保证密钥、凭据和 Project 工作目录在各执行节点一致。若项目目录位于 `/data` 之外，还需为这些目录添加相应挂载。

### Data Plane Deployment 与 Service

以下是仓库中的完整 Data Plane 示例。它运行两个副本，监听容器端口 `8080`，读取 `agw-database` Secret，并将 Service 暴露为 `30820`。Control Plane 的对应文件使用同样的数据卷和数据库配置，副本数为 `1`、NodePort 为 `30816`，并额外读取 `agw-admin` 的 `password` 作为 `Setup__AdminPassword`。

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

使用前需要确认以下配置：

- **镜像**：示例使用 `localhost/agw-…:local` 和 `imagePullPolicy: Never`，需要先构建镜像并加载到 kind 节点。使用镜像仓库时，替换为可拉取的地址和版本，并调整拉取策略及必要的凭据。
- **数据库**：两个角色的 Secret 必须指向同一个可从 Pod 访问的 PostgreSQL。连接字符串中的 `localhost` 指 Pod 自身，通常不是宿主机数据库。
- **权限**：示例为了适配本地目录权限使用 root 身份运行。其他环境应按存储权限配置合适的 UID/GID，不应直接照搬这个本地设置。
- **连接保持**：Service 使用 `ClientIP` 会话亲和性，帮助 SignalR 请求落到同一 Pod。如果 Nginx 位于集群外，多个客户端可能都表现为同一个代理 IP，因此不能据此保证负载均匀。

### 部署顺序

先准备本地镜像、数据目录和两个 Secret 的内容文件，再从仓库根目录执行。Secret 文件只包含对应值，不要将真实凭据提交到仓库。以下命令使用当前 kubectl 上下文的默认 namespace；若选择其他 namespace，Deployment、Service、PVC 和 Secret 必须保持一致。

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

`rollout status` 表示 Deployment 已完成滚动更新，不能单独证明 AGW 已完成初始化。本例由 Control Plane 的 `Setup__AdminPassword` 触发首次初始化；应结合日志和登录页面确认成功后再启动 Data Plane。已有数据库的认证配置不会被该初始密码覆盖。

按上述 kind 端口映射运行时，前面的 Nginx upstream 可直接使用 `127.0.0.1:30816` 和 `127.0.0.1:30820`。如果 Nginx 在集群内部，则使用相同 namespace 下的 Service 地址 `agw-control-plane:30816` 和 `agw-data-plane:30820`。检查 PVC 为 Bound、Pod 正常运行后，再验证登录、执行连接和各节点实际接收的请求。

不要执行 `kubectl apply -f deploy/k8s/`：目录中的 kind Cluster 文件是 `kind` 的输入，不是 Kubernetes API 资源。更改 kind 的端口或目录映射需要重建集群，操作前先备份数据。完整步骤见 [本地 kind 部署说明](https://github.com/zxyao145/agw/blob/main/deploy/k8s/README.md)。

## Nginx 配置示例

下面的配置参考本地 `agw.conf` 的分流方式：Nginx 提供统一入口，Control Plane 监听 `30816`，Data Plane 监听 `30820`。端口只是示例，需要与实际 Host 的监听地址一致；如果服务运行在不同主机或容器中，将 `127.0.0.1` 换成 Nginx 能访问的地址。

### Control Plane 同时提供 Web

将以下内容保存为 Nginx 的站点配置文件，并确保它被 `nginx.conf` 的 **`http {}`** 引入。`map`、`log_format` 和 `upstream` 不能放进 `server {}`。日志路径相对于 Nginx prefix，使用前创建对应目录或替换为可写的绝对路径。

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

此示例使用 HTTP 便于本机验证。对外使用时，在该 `server` 中配置 `listen 443 ssl;`、`ssl_certificate` 和 `ssl_certificate_key`，使用自己的域名与有效证书，并将 HTTP 入口重定向到 HTTPS。

| 请求 | 转发目标 | 作用 |
| --- | --- | --- |
| `/api/hubs/exec` 及其子路径 | Data Plane | SignalR 协商与执行连接 |
| `/a2a/*` | Data Plane | A2A 请求及流式响应 |
| `/.well-known/agents.json` | Data Plane | Agent 发现 |
| 其余路径 | Control Plane | 初始化、管理 API、OpenAPI、Web 页面与静态资源 |

`proxy_pass` 不附加 URI，保留原始路径和查询参数。认证头和 Cookie 默认随请求转发；`Upgrade`、`Connection` 和 HTTP/1.1 用于 WebSocket。关闭执行与 A2A 路由的响应缓冲，避免流式内容被代理积攒后才返回。`3600s` 是代理读写超时设置，不保证任意时长的任务都不会断线。

多 Data Plane 实例时，`ip_hash` 让来自同一 IP 的连接尽量落到同一实例，避免 SignalR 协商和后续连接被分到不同节点；它不能替代共享数据库、执行状态和恢复配置。若 Nginx 前还有代理，需结合实际网络配置可信代理与客户端 IP；不要直接信任来自任意来源的 `X-Forwarded-For`。

示例访问日志使用 `$uri`，不记录查询参数；`upstream` 字段可帮助确认请求实际进入哪个节点。`client_max_body_size` 只控制 Nginx 请求体限制，不会提高 AGW 对图片等附件的限制。

### Web 单独运行

如果与参考配置一样，Web 在 `3001` 单独运行，保留上述 Data Plane 路由和公共代理设置，再添加 `agw_web` upstream，按下面的方式调整管理路由并替换原来的 `location /`。`3001` 是仓库 Web 开发端口，实际部署按 Web 服务端口填写。

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

这样 `/setup` 和 `/setup/` 都会进入 Control Plane，普通 `/api/` 不会误送到 Web；更长的 `/api/hubs/exec` 匹配仍进入 Data Plane。独立 Web 服务自身的后端地址也应指向 Control Plane。客户端统一使用 Nginx 的入口地址。

### 检查并加载配置

保存实际配置后，先检查，再重新加载：

```bash
nginx -t
nginx -s reload
```

重新加载需要在自己的部署环境执行。检查登录和页面资源是否正常；在浏览器网络面板检查执行连接是否成功升级为 WebSocket（`101`），并查看访问日志中的 upstream 是否为 Data Plane。普通管理 API 则应进入 Control Plane。登录失败先核对 Cookie、转发协议和应用的代理信任设置；执行连接失败先检查 Upgrade、路由及 Data Plane 端口。

## 验证

依次验证登录、一次 Chat、一次 Job，并观察实际执行节点。所有 Host 从同一数据库读取初始化和认证状态。重启恢复还依赖共享目录、密钥和运行时凭据一致，不能仅验证容器都已启动。

## 实现与参考

- [Cluster Compose](https://github.com/zxyao145/agw/blob/main/deploy/compose.cluster.yaml)
- [Ingress example](https://github.com/zxyao145/agw/blob/main/deploy/nginx.split.conf.example)
- [Deployment](https://github.com/zxyao145/agw/blob/main/docs/4.Deployment.md)
