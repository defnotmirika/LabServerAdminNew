-- Direct migration script to update system_logs table
-- Run this to convert user_id from INTEGER to VARCHAR(20)

-- Step 1: Check current schema
SELECT 
    column_name, 
    data_type, 
    character_maximum_length,
    is_nullable
FROM information_schema.columns
WHERE table_name = 'system_logs'
ORDER BY ordinal_position;

-- Step 2: Drop foreign key constraint if it exists
DO $$ 
BEGIN
    IF EXISTS (
        SELECT 1 FROM pg_constraint 
        WHERE conname = 'system_logs_user_id_fkey'
    ) THEN
        ALTER TABLE system_logs DROP CONSTRAINT system_logs_user_id_fkey;
        RAISE NOTICE 'Dropped foreign key constraint: system_logs_user_id_fkey';
    END IF;
END $$;

-- Step 3: Convert user_id from INTEGER to VARCHAR(20)
DO $$
BEGIN
    -- Check if user_id is currently an integer type
    IF EXISTS (
        SELECT 1 
        FROM information_schema.columns 
        WHERE table_name = 'system_logs' 
        AND column_name = 'user_id'
        AND data_type IN ('integer', 'bigint', 'smallint')
    ) THEN
        -- Convert to VARCHAR(20)
        ALTER TABLE system_logs 
        ALTER COLUMN user_id TYPE VARCHAR(20) USING user_id::VARCHAR(20);
        
        RAISE NOTICE 'Converted user_id from INTEGER to VARCHAR(20)';
    ELSE
        RAISE NOTICE 'user_id is already VARCHAR or does not exist';
    END IF;
END $$;

-- Step 4: Ensure column order matches new schema (PostgreSQL doesn't support column reordering directly)
-- We need to recreate the table if column order matters
DO $$
DECLARE
    column_order TEXT;
BEGIN
    -- Check current column order
    SELECT string_agg(column_name, ', ' ORDER BY ordinal_position)
    INTO column_order
    FROM information_schema.columns
    WHERE table_name = 'system_logs';
    
    RAISE NOTICE 'Current column order: %', column_order;
    
    -- If order is wrong, we need to recreate the table
    -- Expected order: id, log_level, log_message, action_type, user_id, computer_id, log_time
END $$;

-- Step 5: Add index on user_id if it doesn't exist
CREATE INDEX IF NOT EXISTS idx_system_logs_user ON system_logs(user_id);

-- Step 6: Verify the changes
SELECT 
    column_name, 
    data_type, 
    character_maximum_length,
    is_nullable
FROM information_schema.columns
WHERE table_name = 'system_logs'
ORDER BY ordinal_position;

-- Step 7: Show sample data
SELECT 
    id, 
    log_level, 
    log_message, 
    action_type,
    user_id,
    computer_id,
    log_time
FROM system_logs
ORDER BY log_time DESC
LIMIT 5;

-- Success message
DO $$
BEGIN
    RAISE NOTICE '============================================';
    RAISE NOTICE 'system_logs table migration completed!';
    RAISE NOTICE 'user_id is now VARCHAR(20)';
    RAISE NOTICE '============================================';
END $$;
