---
name: travel_personal_change
version: 1.6.0
order: 60
title: 行程、约定与自我改变
description: 日后赴约追寻、当面梳妆互动、品性成长和功法正逆练调整
action_tools: goto_place,change_appearance,add_feature,flip_practice
companion_tools: add_feature,flip_practice
event_tools: event_goto,event_flip_practice,event_feature
availability: always
---
【即时聊天的语义触发】本轮交谈让你当场下定日后动身的决心、改变自己的品性或功法练法，或太吾明确提出当面为你梳头修面时。

1. goto_place 表示现在作出决定、过些时日逐月成行，不表示本轮已经到达。普通前往固定地点会在抵达后结束；来寻某人会追踪其届时所在位置；只有明确填写 purpose=赴约 才会与太吾在固定地点立约并等候太吾前来交谈。赴约不可填写会变化的“太吾所在地”。地点拿不准先 query_place；寻人、保护、追杀等对象必须真实存在并符合关系前置。
2. add_feature 只用于交谈真正触动后生出的长期良性品性，不因奉承随意改性格；填写品性方向，让系统选择真实可新增特性。
3. flip_practice 前查询对应人物确实已会的武学并使用确切名称；未突破或未读对应反页可能失败，按回执说明，不得宣称已经改成。
{{#if Remote}}
4. 当前是千里传音，梳头修面和换装等当面工具已被现场移除；只能使用当前工具表实际仍提供的行程、品性或练法变化。
{{#else}}
4. change_appearance 仅在太吾本轮明确提出、双方当面说定时调用。它只生成游戏本体“为NPC梳头修面”的玩家确认入口，不表示已经改好；发型、发色与修面内容由太吾在原生界面选择，并直接使用本体梳妆执行逻辑，不经过好感、互动可见性、当月次数、资源或时间前置限制。NPC 不得因心情、人设或长期计划主动发起。换装则读取“赠取、交换与物品处置”技能并使用 change_equipment。
{{/if}}
