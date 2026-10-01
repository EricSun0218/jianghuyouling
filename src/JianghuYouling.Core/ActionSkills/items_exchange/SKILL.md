---
name: items_exchange
version: 1.8.0
order: 10
title: 赠取、交换与物品处置
description: 赠物、收礼、交换、偷取或换装前的真实清单与失败恢复
action_tools: gift,barter,steal,taiwu_give_item,change_equipment
companion_tools: steal,barter,change_equipment,gift_item,gift_silver
event_tools: event_gift,event_gift_silver,event_barter,event_steal,event_equipment
availability: always
---
【即时聊天的语义触发】本轮单聊/群聊中，你理解到太吾正在提议、请求、协商相关处置，或你根据眼前人物与物品当场决定赠物、领受太吾赠物、以物换物、亲自偷取或更换装备时。玩家不必使用固定口令，由你按完整语义判断。

{{#if Remote}}
当前是千里传音：赠物、收礼、交换、偷取和换装都属于当面行为，当前工具表不会提供这些动作，不得口头假装完成，也不得借本技能绕过现场边界。
{{#else}}
1. 先确认行动者、目标和现场。赠取、交换和偷窃必须当面发生；第三方还须是当前允许识别且确实在场的人。
2. 名称与数量必须来自真实清单。gift/change_equipment 用预载的随身清单即可；拿不准再 query_npc_items。taiwu_give_item 先 query_taiwu_items。barter 分别 query_person_items 查双方。steal 先查 victim 的清单。
3. 清单中“物名×3”只把“物名”填入名称参数，把 3 填入数量。资源、银钱与单件物品按工具参数区分，不能用泛称臆造。
4. barter 是一次原子交换，不得用两次赠送假装交换。steal 的偷窃者固定是你这个 NPC；太吾不能借一句话让自己偷 NPC，也不能让你代他充当偷窃者。
5. 买卖、交易和议价不只可以用银钱。双方想成交、钱不够、舍不得收钱或更看重对方某件东西时，可以先查双方持物并主动提出 barter；不必等玩家逐字说出“以物换物”，但也不要在银钱成交更符合双方意愿时强行换物。barter 的任意一边都可以填“银钱”，所以可做物品换物品、物品换银钱或银钱换物品；amount 填真实金额。
6. 一旦正文表示已经赠出、收下、换成、偷到或换上，本轮就必须调用真实动作，不能用动作描写代替回执。工具明确失败仍要给自然正文，并按原因继续：名字不对就回查清单换成确切名；条件不满足就改方案或如实拒绝，绝不假称成功。
7. NPC 主动赠礼只能从未穿戴的背包物、资源或熟食里选，不能为了自行送礼卸下当前兵器、衣甲、佩饰或代步。只有玩家本轮语义上明确要求赠出当前穿戴的具体装备时，gift 才可把 allow_equipped 设为 true；自主赠礼或含糊要求必须省略/设 false，不能虚构玩家授权。
{{/if}}
