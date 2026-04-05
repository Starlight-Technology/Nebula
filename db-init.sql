-- Nebula Database Initialization Script for PostgreSQL

-- Create requests table to track user requests
CREATE TABLE IF NOT EXISTS requests (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    prompt TEXT NOT NULL,
    classification VARCHAR(50) NOT NULL CHECK (classification IN ('Action', 'Chat', 'Unknown')),
    response TEXT,
    created_at TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP
);

-- Create commands table to store validated commands
CREATE TABLE IF NOT EXISTS commands (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    request_id UUID NOT NULL,
    command_id BIGINT,
    objective TEXT NOT NULL,
    command TEXT NOT NULL,
    os_type VARCHAR(20) NOT NULL CHECK (os_type IN ('Windows', 'Linux', 'macOS')),
    executed BOOLEAN DEFAULT FALSE,
    execution_result TEXT,
    created_at TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP,
    FOREIGN KEY (request_id) REFERENCES requests(id) ON DELETE CASCADE
);

-- Create command_verifications table to track verification results
CREATE TABLE IF NOT EXISTS command_verifications (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    command_id UUID NOT NULL,
    is_correct BOOLEAN NOT NULL,
    is_safe BOOLEAN NOT NULL,
    verification_notes TEXT,
    created_at TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP,
    FOREIGN KEY (command_id) REFERENCES commands(id) ON DELETE CASCADE
);

-- Create indexes for common queries
CREATE INDEX idx_requests_created_at ON requests(created_at DESC);
CREATE INDEX idx_requests_classification ON requests(classification);
CREATE INDEX idx_commands_request_id ON commands(request_id);
CREATE INDEX idx_commands_os_type ON commands(os_type);
CREATE INDEX idx_commands_executed ON commands(executed);
CREATE INDEX idx_commands_created_at ON commands(created_at DESC);
CREATE INDEX idx_command_verifications_command_id ON command_verifications(command_id);

-- Create trigger to auto-update updated_at timestamps
CREATE OR REPLACE FUNCTION update_updated_at_column()
RETURNS TRIGGER AS $$
BEGIN
    NEW.updated_at = CURRENT_TIMESTAMP;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER update_requests_updated_at BEFORE UPDATE ON requests
    FOR EACH ROW EXECUTE FUNCTION update_updated_at_column();

CREATE TRIGGER update_commands_updated_at BEFORE UPDATE ON commands
    FOR EACH ROW EXECUTE FUNCTION update_updated_at_column();
