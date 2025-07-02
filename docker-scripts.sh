#!/bin/bash

# Docker操作用のヘルパースクリプト

# 色付き出力
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

# ヘルプメッセージ
show_help() {
    echo "Stratify.S3 Docker Helper Script"
    echo ""
    echo "Usage: ./docker-scripts.sh [command]"
    echo ""
    echo "Commands:"
    echo "  build       - Dockerイメージをビルド"
    echo "  up          - コンテナを起動（本番モード）"
    echo "  up-dev      - コンテナを起動（開発モード、ホットリロード有効）"
    echo "  down        - コンテナを停止して削除"
    echo "  logs        - コンテナのログを表示"
    echo "  shell       - Stratify.S3コンテナにシェルアクセス"
    echo "  clean       - すべてのコンテナ、イメージ、ボリュームを削除"
    echo "  status      - コンテナのステータスを表示"
    echo "  health      - ヘルスチェックステータスを確認"
    echo ""
}

# コマンド実行
case "$1" in
    build)
        echo -e "${GREEN}Building Docker image...${NC}"
        docker-compose build
        ;;
    up)
        echo -e "${GREEN}Starting containers in production mode...${NC}"
        docker-compose up -d
        echo -e "${YELLOW}Application available at: http://localhost:8000${NC}"
        ;;
    up-dev)
        echo -e "${GREEN}Starting containers in development mode...${NC}"
        docker-compose -f docker-compose.yml -f docker-compose.dev.yml up
        ;;
    down)
        echo -e "${YELLOW}Stopping containers...${NC}"
        docker-compose down
        ;;
    logs)
        docker-compose logs -f stratify-s3
        ;;
    shell)
        echo -e "${GREEN}Accessing Stratify.S3 container shell...${NC}"
        docker-compose exec stratify-s3 /bin/bash
        ;;
    clean)
        echo -e "${RED}WARNING: This will remove all containers, images, and volumes!${NC}"
        read -p "Are you sure? (y/N) " -n 1 -r
        echo
        if [[ $REPLY =~ ^[Yy]$ ]]
        then
            docker-compose down -v --rmi all
            echo -e "${GREEN}Cleanup complete${NC}"
        fi
        ;;
    status)
        echo -e "${GREEN}Container status:${NC}"
        docker-compose ps
        ;;
    health)
        echo -e "${GREEN}Health check status:${NC}"
        curl -s http://localhost:8000/health | jq .
        ;;
    *)
        show_help
        ;;
esac