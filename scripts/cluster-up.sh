#!/usr/bin/env bash
# Creates the kind cluster described in k8s/kind-cluster.yaml and starts cloud-provider-kind,
# which implements Ingress (and LoadBalancer Services) for kind clusters.
set -euo pipefail

cd "$(dirname "$0")/.."

CLUSTER=hackernews
CLOUD_PROVIDER_IMAGE=registry.k8s.io/cloud-provider-kind/cloud-controller-manager:v0.11.1

if ! kind get clusters | grep -qx "$CLUSTER"; then
  kind create cluster --config k8s/kind-cluster.yaml
fi

kubectl config use-context "kind-$CLUSTER"

# The API runs on the control-plane node, which kubeadm excludes from load balancers by default.
kubectl label node "$CLUSTER-control-plane" node.kubernetes.io/exclude-from-external-load-balancers- 2>/dev/null || true

if ! docker ps --format '{{.Names}}' | grep -qx cloud-provider-kind; then
  docker run -d --rm --name cloud-provider-kind --network kind \
    -v /var/run/docker.sock:/var/run/docker.sock \
    "$CLOUD_PROVIDER_IMAGE" --enable-lb-port-mapping
fi

kubectl get nodes
