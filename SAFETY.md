# Safety and limitations

Experimental independent software; not affiliated with or supported by LG. It sends undocumented volatile monitor-control commands. Reduced cooling or unexpected device behavior may damage equipment. Use at your own risk.

- The latest Quiet path was tested on **one LG UltraGear 39GX950B**: USB `043E:9C33`, revision `0111`, scaler `3.11.0`, MICOM `1.11`. This does not validate other models or firmware. Connect only one matching monitor over USB.
- Start.cmd records the current operator's explicit confirmation that the service menu is closed. This observation expires after 30 minutes. The app may briefly open a service context and closes it automatically; a software Close is not independent physical verification.
- One explicit request performs at most one lowering sequence, capped at 10 seconds with early stopping. The initial state is prepared automatically; an absent USB connection is awaited for up to two minutes.
- Successful Apply and exit deliberately ends all helper processes **without restoring automatic control**. There is no resident thermal monitor, background keeper or repeated automatic lowering. Closing or losing a process before successful completion may invoke recovery, but complete fault/Auto restoration is not qualified as reliable.
- Cached RPM may freeze and is not a current fan-speed measurement. Temperature, adequate cooling, thermal protection, acoustic repeatability and persistence after power cycling have not been verified. Software completion does not establish these properties.
- USB loss, driver failures, power loss and compound process faults can prevent software recovery. Stop using the utility if cooling or monitor behavior appears abnormal.
- The app does not flash firmware or start/restart LG Switch. Close other applications that control this monitor before use. The legacy duty slider and `--set` are not supported for the 39GX950B controller.

Русский: экспериментальная программа использует недокументированные команды. После успешного применения она выходит без возврата в Авто. Свежие обороты, температура и тепловая защита не контролируются; полный возврат при сбоях и сохранение после отключения питания не подтверждены. Не используйте программу для других моделей или версий прошивки на основании одного совпадения USB-идентификатора.
