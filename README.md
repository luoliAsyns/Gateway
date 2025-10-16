## Description
一个关于代下单的程序集合
第一次开源，很多不规范，有问题可以提issue

主要部分：
  1. Call Agiso api
  2. Call Sexytea api
  3. ExternalOrder/Coupon/ConsumeInfo 的CRUD
  4. RabbitMQ解耦

一些技术：
  1. redis缓存，也会用到pub/sub
  2. JWT (目前是单token)
  3. RabbitMQ 解耦


此项目是请求的入口
因为存在很多第三方api，所以没有使用grpc，因为结构可能会经常变；
同时内部使用rabbitmq而不是直接call api，是为了更新某一个service的过程中，可以让mq暂存消息，而不丢失
同时保留了webapi请求的接口，是为了方便给后台保留人工操作

## Flow in business
整体的流程：
1. 客户下单获取短链
```mermaid
sequenceDiagram
    用户->>+淘宝: 下单
    淘宝->>+后台: 完整订单
    后台->>+淘宝: 短链
    后台->>+淘宝: 订单发货
```
2. 客户在短链中消费
```mermaid
sequenceDiagram
    用户->>+后台: 打开链接，后台校验卡密
    后台->>+用户: 部分淘宝订单信息(核心是可用额度)
    用户->>+后台: 消费信息
    后台->>+后台: 验证消费信息
    后台->>+第三方平台: 订单
    第三方平台->>后台:订单结果
    后台->>+用户:刷新订单
```

## Flow in code
服务的流转：

1. 客户下单获取短链
```mermaid
flowchart TD
     ag[agiso]-->|OrderCreate| gw[Gateway]
     gw --> |ExternalOrderDTO| mq
     mq --> |ExternalOrderDTO| eo[ExternalOrder Service]
     eo --> |ExternalOrderInserted| mq[rabbitMQ]
     eo --> |ExternalOrderEntity| db[mysql]

     mq[rabbitMQ] --> |ExternalOrderInserted| cp[Coupon Service]
     cp --> |CoupontEntity| db
     cp --> |CouponGenerated|mq

     mq --> |CouponGenerated| sp[ShipBOT]

     sp --> |result: Geneate link & Ship| gw

```


2. 客户在短链中消费 
2.1 校验
```mermaid
flowchart TD
     web[web]-->|coupons| gw[Gateway]
     gw --> |coupons| c[Coupon Service]
     c --> |coupons view model| gw
     gw --> |coupons view model| web
```
2.2 消费
```mermaid
flowchart TD
     web[web]-->|ConsumeInfo| gw[Gateway]
     gw --> |ConsumeInfoDTO| mq
     mq --> |ConsumeInfoDTO| ci[ConsumeInfo Service]
     ci --> |ConsumeInfoInserted| mq[rabbitMQ]
     ci --> |ConsumeInfoEntity| db[mysql]

     mq[rabbitMQ] --> |ConsumeInfoDTO| po[PlaceOrderBOT]
     po -->|ThirdPartyOrder info| gw

```
