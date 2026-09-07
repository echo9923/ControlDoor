-- ============================================================
-- 专项：清除工号为 0 的门禁进出事件
-- 前提条件：人员表 system_users 中不存在工号为 '0' 的有效员工
-- 执行效果：硬删除 attendance_gate_v2 中 username = '0' 的全部记录
-- 可重复执行：是（无匹配行时 DELETE 不报错）
-- ============================================================

SET NOCOUNT ON;
SET XACT_ABORT ON;

-- 检查和删除必须位于同一批次、同一事务；GO 后的批次不会被前面的 RETURN 阻止。
BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'dbo.system_users', N'U') IS NULL
        THROW 51000, N'缺少 system_users 表，拒绝执行删除。', 1;
    IF OBJECT_ID(N'dbo.attendance_gate_v2', N'U') IS NULL
        THROW 51001, N'缺少 attendance_gate_v2 表，拒绝执行删除。', 1;

    IF EXISTS (
        SELECT 1
        FROM dbo.system_users WITH (UPDLOCK, HOLDLOCK)
        WHERE username = N'0'
          AND deleted = 0
    )
        THROW 51002, N'人员表 system_users 中存在工号为 0 的有效员工，拒绝执行删除。请先确认业务意图。', 1;

    DELETE FROM dbo.attendance_gate_v2
    WHERE username = N'0';

    DECLARE @affectedRows BIGINT = ROWCOUNT_BIG();
    COMMIT TRANSACTION;
    PRINT N'删除完成，实际影响行数: ' + CAST(@affectedRows AS NVARCHAR(20));
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
GO
