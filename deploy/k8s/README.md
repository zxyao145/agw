# Local kind deployment

These manifests target the single-node cluster in `kind-agw-cluster.yaml`.
Keep that file: it maps host ports 30816/30820 and mounts host `/opt/agw`
into the kind node. The storage path is:

```text
Host /opt/agw/agw-data → kind node /opt/agw/agw-data → PV → PVC agw-data → Pod /data
```

PV/PVC does not replace the kind mount. Without `extraMounts`, the hostPath
PV uses storage inside the node container, which is lost when that container
is removed. `ReadWriteOnce` allows both roles and multiple replicas to mount
the volume on the same node; this hostPath is not shared storage across nodes.

Create `/opt/agw/agw-data` on the container runtime host before creating the
cluster. When using a Docker/Podman VM, make sure `/opt/agw` is available to
that VM through its file-sharing configuration.

From the repository root:

```bash
kind create cluster --name agw --config deploy/k8s/kind-agw-cluster.yaml
kubectl apply -f deploy/k8s/agw-data-pv-pvc.yaml
```

Load the local images into the cluster and create Secrets `agw-database`
(key `connection-string`) and `agw-admin` (key `password`). Both roles require
the same reachable PostgreSQL database. Then deploy Control Plane first:

```bash
kubectl apply -f deploy/k8s/agw-control-plane-deployment.yaml
```

After Control Plane initialization completes, deploy Data Plane:

```bash
kubectl apply -f deploy/k8s/agw-data-plane-deployment.yaml
```

Do not apply the whole directory with `kubectl apply -f deploy/k8s/`:
the kind Cluster configuration is input to `kind`, not a Kubernetes resource.
Changes to kind mounts or port mappings require recreating the cluster;
back up existing data first. For multi-node deployments, replace this hostPath
PV with genuinely shared storage supported by the cluster.
